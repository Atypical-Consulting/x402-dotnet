using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;

namespace X402.AspNetCore.Engine;

/// <summary>
/// Holds a paid response in memory until settlement decides whether it may be delivered.
/// </summary>
/// <remarks>
/// Beyond <c>MaxBufferedResponseBytes</c> the write that crosses the cap forces the engine to
/// settle right there, before any byte of the response has left the process — so a settlement that
/// fails exactly at that moment is still caught cleanly; nothing has been streamed yet, and the
/// request can still be refused (see <see cref="Poisoned"/>). Only once settlement *succeeds* at
/// the cap are the held bytes flushed and every later byte streamed straight through. The real
/// trade-off of ADR D5 is therefore not that a failed settlement can no longer withhold content —
/// it can, right up to the cap — but that the withhold-or-deliver decision is made once, on
/// whatever content the buffer holds at that instant, and is never reconsidered as more content is
/// produced. That is what keeps large downloads and server-sent events possible.
/// </remarks>
internal sealed class BufferingResponseBodyFeature : IHttpResponseBodyFeature
{
    private readonly IHttpResponseBodyFeature inner;
    private readonly long cap;
    private readonly Func<CancellationToken, Task<bool>> onOverflowAsync;

    private MemoryStream? buffer = new();
    private CapacityCheckingStream? capacityCheckingStream;
    private PipeWriter? writer;

    public BufferingResponseBodyFeature(
        IHttpResponseBodyFeature inner, long cap,
        Func<CancellationToken, Task<bool>> onOverflowAsync)
    {
        this.inner = inner;
        this.cap = cap;
        this.onOverflowAsync = onOverflowAsync;
    }

    /// <summary>Whether the response has already started streaming to the client.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>
    /// Set once settlement was attempted — and failed — at the instant the cap was crossed. A
    /// terminal state: it is never cleared. Every write attempted after this point fails without
    /// invoking settlement again, and the content held up to that point is gone (see
    /// <see cref="Discard"/>). Consulted directly by the pipeline as well as by this class, because
    /// an endpoint that wraps its own writes in a broad catch (a common pattern for tolerating a
    /// client disconnect) can swallow the exception this state causes and return normally, and the
    /// pipeline must not then fall through into settling — and paying — a second time.
    /// </summary>
    public bool Poisoned { get; private set; }

    /// <summary>Number of bytes currently held.</summary>
    public long BufferedLength => buffer?.Length ?? 0;

    public Stream Stream
    {
        get
        {
            if (Poisoned)
            {
                throw new BufferingSettlementFailedException();
            }

            return Overflowed ? inner.Stream : CapacityChecked();
        }
    }

    public PipeWriter Writer
    {
        get
        {
            if (Poisoned)
            {
                throw new BufferingSettlementFailedException();
            }

            return Overflowed
                ? inner.Writer
                : writer ??= PipeWriter.Create(CapacityChecked(), new StreamPipeWriterOptions(leaveOpen: true));
        }
    }

    public Task CompleteAsync() => Overflowed ? inner.CompleteAsync() : Task.CompletedTask;

    public void DisableBuffering() { /* the engine drives buffering, not the application */ }

    public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken)
        => Overflowed
            ? inner.SendFileAsync(path, offset, count, cancellationToken)
            : OverflowThenAsync(ct => inner.SendFileAsync(path, offset, count, ct), cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken = default)
        => Overflowed ? inner.StartAsync(cancellationToken) : Task.CompletedTask;

    /// <summary>Called by the engine after a successful settlement: releases the held bytes.</summary>
    public async Task FlushBufferAsync(CancellationToken cancellationToken)
    {
        if (Overflowed || buffer is null)
        {
            return;
        }

        if (writer is not null)
        {
            await writer.FlushAsync(cancellationToken);
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(inner.Stream, cancellationToken);
        await inner.Stream.FlushAsync(cancellationToken);
        Discard();
    }

    /// <summary>Called by the engine after a failed settlement: the content is never delivered.</summary>
    public void Discard()
    {
        buffer?.Dispose();
        buffer = null;
        capacityCheckingStream = null;
        writer = null;
    }

    /// <summary>
    /// Checked after every write into the buffer (see <see cref="CapacityCheckingStream"/>, the
    /// only caller). Grows past the cap: settle now, flush, and stream from here on.
    /// </summary>
    /// <remarks>
    /// <see cref="IHttpResponseBodyFeature"/> gives no hook that fires after a write completes —
    /// the application writes straight into <see cref="Stream"/> or <see cref="Writer"/> — so the
    /// cap cannot be enforced from this class alone. <see cref="CapacityCheckingStream"/> is the
    /// control point: it delegates every write to the real <see cref="MemoryStream"/> and then
    /// calls back here.
    /// </remarks>
    internal async Task<bool> CheckCapacityAsync(CancellationToken cancellationToken)
    {
        // Poisoned is terminal: never re-invoke settlement once it has already been attempted and
        // failed here, however this got called again (a stale Stream/Writer reference held across
        // the failing write, or SendFileAsync's own path below).
        if (Poisoned)
        {
            return false;
        }

        if (Overflowed || buffer is null || buffer.Length <= cap)
        {
            return true;
        }

        var settled = await onOverflowAsync(cancellationToken);
        if (!settled)
        {
            // Settlement was genuinely attempted here and failed: record that permanently before
            // returning, not just for this call. Discarding now — rather than leaving the buffer
            // to be discarded later by whoever observes the failure — means a caller who ignores
            // the false/exception cannot go on appending to it either.
            Poisoned = true;
            Discard();
            return false;
        }

        var held = buffer;
        Overflowed = true;
        buffer = null;
        capacityCheckingStream = null;
        writer = null;

        held.Position = 0;
        await held.CopyToAsync(inner.Stream, cancellationToken);
        await held.DisposeAsync();
        return true;
    }

    private async Task OverflowThenAsync(
        Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        if (!await CheckCapacityAsync(cancellationToken))
        {
            throw new BufferingSettlementFailedException();
        }

        await action(cancellationToken);
    }

    private Stream CapacityChecked() => capacityCheckingStream ??= new CapacityCheckingStream(buffer!, this);

    /// <summary>
    /// Wraps the in-memory buffer so every write is followed by <see cref="CheckCapacityAsync"/> —
    /// the hook <see cref="IHttpResponseBodyFeature"/> itself does not provide. Every member simply
    /// delegates to the wrapped <see cref="MemoryStream"/>, plus that one call after a write.
    /// </summary>
    private sealed class CapacityCheckingStream(MemoryStream inner, BufferingResponseBodyFeature owner)
        : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            // Stream has no synchronous capacity hook to call into: block on the (usually already
            // completed) check rather than leave a write path that never enforces the cap. Safe
            // here — no SynchronizationContext is present on the Kestrel/TestServer request path —
            // but callers should prefer WriteAsync.
            if (!owner.CheckCapacityAsync(CancellationToken.None).GetAwaiter().GetResult())
            {
                throw new BufferingSettlementFailedException();
            }
        }

        public override async Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer, offset, count, cancellationToken);
            if (!await owner.CheckCapacityAsync(cancellationToken))
            {
                throw new BufferingSettlementFailedException();
            }
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            if (!await owner.CheckCapacityAsync(cancellationToken))
            {
                throw new BufferingSettlementFailedException();
            }
        }
    }
}

/// <summary>
/// Thrown by <see cref="BufferingResponseBodyFeature"/> into the write call that crossed the cap,
/// when the settlement that crossing forced then failed.
/// </summary>
/// <remarks>
/// <see cref="BufferingResponseBodyFeature.CheckCapacityAsync"/> has no return path back to
/// whatever unrelated code called <c>Stream.WriteAsync</c> or <c>PipeWriter</c> other than the
/// write call itself failing — there is no reader of a plain <c>false</c> return value once it is
/// buried inside a byte-count write. Throwing here lets the caller distinguish this from an
/// unrelated endpoint fault: nothing reached the network yet, so the request can still be refused
/// with a clean 402 instead of a 500, and — unlike an endpoint bug — the authorization must not be
/// abandoned, because settlement was genuinely attempted and its (failed) outcome is already on
/// record in the ledger.
/// </remarks>
internal sealed class BufferingSettlementFailedException()
    : Exception("settlement failed while the buffered response was still withholdable");
