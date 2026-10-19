using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;
using X402.AspNetCore.Engine;

namespace X402.AspNetCore.Tests;

/// <summary>
/// Drives <see cref="BufferingResponseBodyFeature"/> directly, against a recording inner feature,
/// for the stream-level paths the HTTP fixture never reaches: synchronous and array-based writes,
/// flushes through a cached body, and the overflowed/poisoned/discarded states. <c>BufferingTests</c>
/// covers the same class end to end through <c>PaidServerFixture</c>.
/// </summary>
public sealed class BufferingResponseBodyFeatureTests
{
    private const int Cap = 4;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum WriteKind
    {
        Sync,
        ArrayAsync,
        MemoryAsync,
    }

    [Theory]
    [InlineData(WriteKind.Sync)]
    [InlineData(WriteKind.ArrayAsync)]
    [InlineData(WriteKind.MemoryAsync)]
    public async Task A_write_under_the_cap_is_released_by_FlushBufferAsync(WriteKind kind)
    {
        var harness = new Harness();
        await WriteAsync(harness.Feature.Stream, kind, Bytes(Cap));

        await harness.Feature.FlushBufferAsync(Ct);

        harness.Inner.Body.ToArray().ShouldBe(Bytes(Cap));
    }

    [Theory]
    [InlineData(WriteKind.Sync)]
    [InlineData(WriteKind.ArrayAsync)]
    [InlineData(WriteKind.MemoryAsync)]
    public async Task A_write_crossing_the_cap_settles_and_forwards_the_held_bytes(WriteKind kind)
    {
        var harness = new Harness();

        await WriteAsync(harness.Feature.Stream, kind, Bytes(Cap + 1));

        harness.Inner.Body.ToArray().ShouldBe(Bytes(Cap + 1));
    }

    [Theory]
    [InlineData(WriteKind.Sync)]
    [InlineData(WriteKind.ArrayAsync)]
    [InlineData(WriteKind.MemoryAsync)]
    public async Task A_write_crossing_the_cap_throws_when_settlement_fails(WriteKind kind)
    {
        var harness = new Harness(settles: false);

        await Should.ThrowAsync<BufferingSettlementFailedException>(
            () => WriteAsync(harness.Feature.Stream, kind, Bytes(Cap + 1)));
    }

    [Theory]
    [InlineData(WriteKind.Sync)]
    [InlineData(WriteKind.ArrayAsync)]
    [InlineData(WriteKind.MemoryAsync)]
    public async Task A_cached_body_retains_nothing_in_memory_once_streaming(WriteKind kind)
    {
        // A long stream (server-sent events, a large download) written through a Response.Body
        // reference fetched before the cap must not keep accumulating in the emptied buffer.
        var harness = new Harness();
        var cached = harness.Feature.Stream;
        await WriteAsync(cached, kind, Bytes(Cap + 1));

        await WriteAsync(cached, kind, Bytes(Cap));

        cached.Length.ShouldBe(0);
    }

    [Fact]
    public async Task A_cached_body_keeps_forwarding_writes_once_streaming()
    {
        var harness = new Harness();
        var cached = harness.Feature.Stream;
        await cached.WriteAsync(Bytes(Cap + 1), Ct);

        await cached.WriteAsync(Bytes(Cap), Ct);

        harness.Inner.Body.Length.ShouldBe((2 * Cap) + 1);
    }

    [Fact]
    public async Task A_cached_body_flushes_the_real_stream_once_streaming()
    {
        var harness = new Harness();
        var cached = harness.Feature.Stream;
        await cached.WriteAsync(Bytes(Cap + 1), Ct);

        await cached.FlushAsync(Ct);

        harness.Inner.Body.Flushes.ShouldBe(1);
    }

    [Fact]
    public async Task A_cached_body_flushes_the_real_stream_synchronously_once_streaming()
    {
        var harness = new Harness();
        var cached = harness.Feature.Stream;
        await cached.WriteAsync(Bytes(Cap + 1), Ct);

        cached.Flush();

        harness.Inner.Body.Flushes.ShouldBe(1);
    }

    [Fact]
    public async Task A_cached_body_written_after_poisoning_throws_the_settlement_failure()
    {
        var harness = new Harness(settles: false);
        var cached = harness.Feature.Stream;
        await harness.PoisonAsync();

        await Should.ThrowAsync<BufferingSettlementFailedException>(
            () => cached.WriteAsync(Bytes(1), Ct).AsTask());
    }

    [Fact]
    public async Task A_cached_body_written_after_discard_fails_instead_of_buffering_into_a_dropped_buffer()
    {
        var harness = new Harness();
        var cached = harness.Feature.Stream;
        harness.Feature.Discard();

        await Should.ThrowAsync<ObjectDisposedException>(() => cached.WriteAsync(Bytes(1), Ct).AsTask());
    }

    [Fact]
    public void Stream_returns_the_same_capacity_checked_stream_while_buffering()
    {
        var harness = new Harness();

        harness.Feature.Stream.ShouldBeSameAs(harness.Feature.Stream);
    }

    [Fact]
    public async Task Stream_is_the_real_stream_once_streaming()
    {
        var harness = new Harness();
        await harness.OverflowAsync();

        harness.Feature.Stream.ShouldBeSameAs(harness.Inner.Body);
    }

    [Fact]
    public async Task Writer_is_the_real_writer_once_streaming()
    {
        var harness = new Harness();
        await harness.OverflowAsync();

        harness.Feature.Writer.ShouldBeSameAs(harness.Inner.Writer);
    }

    [Fact]
    public async Task Stream_throws_once_poisoned()
    {
        var harness = new Harness(settles: false);
        await harness.PoisonAsync();

        Should.Throw<BufferingSettlementFailedException>(() => harness.Feature.Stream);
    }

    [Fact]
    public async Task Writer_throws_once_poisoned()
    {
        var harness = new Harness(settles: false);
        await harness.PoisonAsync();

        Should.Throw<BufferingSettlementFailedException>(() => harness.Feature.Writer);
    }

    [Fact]
    public async Task CompleteAsync_does_not_complete_the_real_response_while_buffering()
    {
        var harness = new Harness();

        await harness.Feature.CompleteAsync();

        harness.Inner.Completes.ShouldBe(0);
    }

    [Fact]
    public async Task CompleteAsync_completes_the_real_response_once_streaming()
    {
        var harness = new Harness();
        await harness.OverflowAsync();

        await harness.Feature.CompleteAsync();

        harness.Inner.Completes.ShouldBe(1);
    }

    [Fact]
    public async Task StartAsync_starts_the_real_response_once_streaming()
    {
        var harness = new Harness();
        await harness.OverflowAsync();

        await harness.Feature.StartAsync(Ct);

        harness.Inner.Starts.ShouldBe(1);
    }

    [Fact]
    public async Task SendFileAsync_goes_to_the_real_response_once_streaming()
    {
        var harness = new Harness();
        await harness.OverflowAsync();

        await harness.Feature.SendFileAsync("unused.txt", 0, null, Ct);

        harness.Inner.SendFiles.ShouldBe(1);
    }

    [Fact]
    public async Task FlushBufferAsync_pushes_an_unflushed_writer_through()
    {
        var harness = new Harness();
        var writer = harness.Feature.Writer;
        Bytes(2).CopyTo(writer.GetSpan(2));
        writer.Advance(2);

        await harness.Feature.FlushBufferAsync(Ct);

        harness.Inner.Body.Length.ShouldBe(2);
    }

    [Fact]
    public async Task FlushBufferAsync_flushes_the_real_stream()
    {
        var harness = new Harness();
        await harness.Feature.Stream.WriteAsync(Bytes(1), Ct);

        await harness.Feature.FlushBufferAsync(Ct);

        harness.Inner.Body.Flushes.ShouldBe(1);
    }

    [Fact]
    public async Task FlushBufferAsync_releases_the_held_bytes_only_once()
    {
        var harness = new Harness();
        await harness.Feature.Stream.WriteAsync(Bytes(Cap), Ct);
        await harness.Feature.FlushBufferAsync(Ct);

        await harness.Feature.FlushBufferAsync(Ct);

        harness.Inner.Body.Length.ShouldBe(Cap);
    }

    [Fact]
    public async Task FlushBufferAsync_after_discard_delivers_nothing()
    {
        var harness = new Harness();
        await harness.Feature.Stream.WriteAsync(Bytes(1), Ct);
        harness.Feature.Discard();

        await harness.Feature.FlushBufferAsync(Ct);

        harness.Inner.Body.Length.ShouldBe(0);
    }

    [Fact]
    public async Task FlushBufferAsync_after_poisoning_delivers_nothing()
    {
        // The content held when settlement failed at the cap must never reach the client, even if
        // something later asks for the buffer to be released.
        var harness = new Harness(settles: false);
        await harness.PoisonAsync();

        await harness.Feature.FlushBufferAsync(Ct);

        harness.Inner.Body.Length.ShouldBe(0);
    }

    [Fact]
    public async Task CheckCapacityAsync_does_not_settle_at_exactly_the_cap()
    {
        var harness = new Harness();

        await harness.Feature.Stream.WriteAsync(Bytes(Cap), Ct);

        harness.Settlements.ShouldBe(0);
    }

    [Fact]
    public async Task CheckCapacityAsync_stays_false_once_poisoned()
    {
        var harness = new Harness(settles: false);
        await harness.PoisonAsync();

        (await harness.Feature.CheckCapacityAsync(Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task CheckCapacityAsync_after_discard_has_nothing_to_settle()
    {
        var harness = new Harness();
        harness.Feature.Discard();

        (await harness.Feature.CheckCapacityAsync(Ct)).ShouldBeTrue();
    }

    [Fact]
    public void BufferingSettlementFailedException_explains_itself()
    {
        new BufferingSettlementFailedException().Message.ShouldContain("settlement failed");
    }

    private static byte[] Bytes(int count) => Enumerable.Range(1, count).Select(i => (byte)i).ToArray();

    private static async Task WriteAsync(Stream stream, WriteKind kind, byte[] bytes)
    {
        switch (kind)
        {
            case WriteKind.Sync:
                stream.Write(bytes, 0, bytes.Length);
                break;
            case WriteKind.ArrayAsync:
                await stream.WriteAsync(bytes, 0, bytes.Length, Ct);
                break;
            default:
                await stream.WriteAsync(bytes.AsMemory(), Ct);
                break;
        }
    }

    private sealed class Harness
    {
        public Harness(bool settles = true)
        {
            Feature = new BufferingResponseBodyFeature(Inner, Cap, _ =>
            {
                Settlements++;
                return Task.FromResult(settles);
            });
        }

        public RecordingBodyFeature Inner { get; } = new();

        public BufferingResponseBodyFeature Feature { get; }

        public int Settlements { get; private set; }

        public async Task OverflowAsync() => await Feature.Stream.WriteAsync(Bytes(Cap + 1), Ct);

        public async Task PoisonAsync()
        {
            try
            {
                await Feature.Stream.WriteAsync(Bytes(Cap + 1), Ct);
            }
            catch (BufferingSettlementFailedException)
            {
            }
        }
    }

    private sealed class RecordingBodyFeature : IHttpResponseBodyFeature
    {
        public CountingStream Body { get; } = new();

        public Stream Stream => Body;

        public PipeWriter Writer => field ??= PipeWriter.Create(Body);

        public int Starts { get; private set; }

        public int Completes { get; private set; }

        public int SendFiles { get; private set; }

        public Task CompleteAsync()
        {
            Completes++;
            return Task.CompletedTask;
        }

        public void DisableBuffering() { }

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken)
        {
            SendFiles++;
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Starts++;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingStream : MemoryStream
    {
        public int Flushes { get; private set; }

        public override void Flush() => Flushes++;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            return Task.CompletedTask;
        }
    }
}
