using System.Numerics;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using X402.Assets;
using X402.Networks;
using X402.Protocol;

namespace X402.OnChain.Tests;

/// <summary>
/// A local Anvil node forking Base Sepolia at a pinned block, so Circle's real <c>FiatToken</c>
/// contracts for EURC and USDC run unchanged at their real addresses. Started in Docker through
/// Testcontainers, or — when <c>X402_ANVIL_RPC_URL</c> is set — an already-running fork is used
/// instead (a machine with Foundry installed but no Docker).
/// </summary>
public sealed class AnvilFork : IAsyncLifetime
{
    /// <summary>Anvil's documented prefunded dev account #0 — the test facilitator's key.</summary>
    public const string DevAccount0Key = "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";

    /// <summary>The pinned fork block (0x2e10000), checked to serve both tokens' <c>masterMinter()</c>.</summary>
    public const long ForkBlock = 48_300_032;

    private const string Image = "ghcr.io/foundry-rs/foundry:v1.8.4";
    private const long ChainId = 84532;

    // Hand-written ABI fragment: only the FiatToken functions these tests touch.
    private const string Abi = """
        [
          {"type":"function","name":"name","stateMutability":"view","inputs":[],"outputs":[{"name":"","type":"string"}]},
          {"type":"function","name":"balanceOf","stateMutability":"view","inputs":[{"name":"account","type":"address"}],"outputs":[{"name":"","type":"uint256"}]},
          {"type":"function","name":"authorizationState","stateMutability":"view","inputs":[{"name":"authorizer","type":"address"},{"name":"nonce","type":"bytes32"}],"outputs":[{"name":"","type":"bool"}]},
          {"type":"function","name":"masterMinter","stateMutability":"view","inputs":[],"outputs":[{"name":"","type":"address"}]},
          {"type":"function","name":"configureMinter","stateMutability":"nonpayable","inputs":[{"name":"minter","type":"address"},{"name":"minterAllowedAmount","type":"uint256"}],"outputs":[{"name":"","type":"bool"}]},
          {"type":"function","name":"mint","stateMutability":"nonpayable","inputs":[{"name":"to","type":"address"},{"name":"amount","type":"uint256"}],"outputs":[{"name":"","type":"bool"}]},
          {"type":"function","name":"transfer","stateMutability":"nonpayable","inputs":[{"name":"to","type":"address"},{"name":"value","type":"uint256"}],"outputs":[{"name":"","type":"bool"}]},
          {"type":"function","name":"transferWithAuthorization","stateMutability":"nonpayable","inputs":[{"name":"from","type":"address"},{"name":"to","type":"address"},{"name":"value","type":"uint256"},{"name":"validAfter","type":"uint256"},{"name":"validBefore","type":"uint256"},{"name":"nonce","type":"bytes32"},{"name":"v","type":"uint8"},{"name":"r","type":"bytes32"},{"name":"s","type":"bytes32"}],"outputs":[]}
        ]
        """;

    private static readonly HexBigInteger Gas = new(200_000);
    private static readonly HexBigInteger NoValue = new(0);

    private IContainer? container;
    private Web3 node = null!;
    private Web3 facilitator = null!;

    /// <summary>The fork's JSON-RPC endpoint.</summary>
    public string RpcUrl { get; private set; } = "";

    /// <summary>The address <see cref="DevAccount0Key"/> signs as.</summary>
    public string FacilitatorAddress { get; } = new Account(DevAccount0Key).Address;

    public async ValueTask InitializeAsync()
    {
        // The fixture is built before any test body runs its RequireOptIn: without the opt-in it
        // must not touch Docker at all. With it, a missing Docker fails loudly — never a skip.
        if (!OnChain.IsOptedIn)
        {
            return;
        }

        RpcUrl = Environment.GetEnvironmentVariable("X402_ANVIL_RPC_URL") is { Length: > 0 } external
            ? external
            : await StartContainerAsync();
        node = new Web3(RpcUrl);
        facilitator = new Web3(new Account(DevAccount0Key, ChainId), RpcUrl);

        var chainId = (await node.Eth.ChainId.SendRequestAsync()).Value;
        if (chainId != ChainId)
        {
            throw new InvalidOperationException(
                $"{RpcUrl} reports chain id {chainId}, not {ChainId}: start anvil with --chain-id {ChainId} on a Base Sepolia fork.");
        }

        // A fork starts at the pinned block's timestamp, hours or weeks behind the wall clock the
        // server and client sign against: align the chain clock, or every authorization would be
        // "not yet valid" on chain.
        await SyncClockAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    private async Task<string> StartContainerAsync()
    {
        var forkUrl = Environment.GetEnvironmentVariable("X402_BASE_SEPOLIA_RPC_URL") is { Length: > 0 } url
            ? url
            : "https://sepolia.base.org";

        container = new ContainerBuilder(Image)
            .WithPortBinding(8545, true)
            // The image's entrypoint is `/bin/sh -c`, so the whole command is one string.
            .WithCommand($"anvil --host 0.0.0.0 --port 8545 --fork-url {forkUrl} --fork-block-number {ForkBlock} --chain-id {ChainId}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPort(8545)
                .WithMethod(HttpMethod.Post)
                .WithContent(() => new StringContent(
                    """{"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]}""",
                    Encoding.UTF8, "application/json"))))
            .Build();

        await container.StartAsync(TestContext.Current.CancellationToken);
        return $"http://{container.Hostname}:{container.GetMappedPublicPort(8545)}";
    }

    public Task<string> NameAsync(AssetDescriptor asset) =>
        Function(node, asset, "name").CallAsync<string>();

    public Task<BigInteger> BalanceOfAsync(AssetDescriptor asset, string holder) =>
        Function(node, asset, "balanceOf").CallAsync<BigInteger>(holder);

    public Task<bool> AuthorizationStateAsync(AssetDescriptor asset, string authorizer, string nonceHex) =>
        Function(node, asset, "authorizationState").CallAsync<bool>(authorizer, Bytes32(nonceHex));

    /// <summary>Mints through FiatToken's own flow: the master minter configures itself, then mints.</summary>
    public async Task MintAsync(AssetDescriptor asset, string to, BigInteger amount)
    {
        var masterMinter = await Function(node, asset, "masterMinter").CallAsync<string>();
        await ImpersonateAsync(masterMinter);
        await SendAsImpersonatedAsync(asset, masterMinter, "configureMinter", masterMinter, amount);
        await SendAsImpersonatedAsync(asset, masterMinter, "mint", to, amount);
    }

    /// <summary>Moves <paramref name="from"/>'s tokens away without its key (Anvil impersonation).</summary>
    public async Task ImpersonatedTransferAsync(AssetDescriptor asset, string from, string to, BigInteger amount)
    {
        await ImpersonateAsync(from);
        await SendAsImpersonatedAsync(asset, from, "transfer", to, amount);
    }

    /// <summary>Warps the chain clock forward and mines a block at the new time.</summary>
    public async Task IncreaseTimeAsync(long seconds)
    {
        await node.Client.SendRequestAsync<object>("evm_increaseTime", null, seconds);
        await node.Client.SendRequestAsync<object>("evm_mine");
    }

    /// <summary>Sets the chain clock back to the wall clock and mines a block at it.</summary>
    public async Task SyncClockAsync()
    {
        await node.Client.SendRequestAsync<object>("evm_setTime", null, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await node.Client.SendRequestAsync<object>("evm_mine");
    }

    /// <summary>Simulates <c>transferWithAuthorization</c> from the facilitator account; throws on revert.</summary>
    public Task SimulateAuthorizationAsync(AssetDescriptor asset, PaymentPayload payload) =>
        node.Eth.Transactions.Call.SendRequestAsync(
            Function(node, asset, "transferWithAuthorization")
                .CreateCallInput(FacilitatorAddress, Gas, NoValue, AuthorizationArguments(payload)));

    /// <summary>
    /// Submits a signed authorization as the facilitator account, with an explicit gas limit so a
    /// reverting call is still mined (status 0) rather than refused at gas estimation.
    /// </summary>
    public Task<TransactionReceipt> SubmitAuthorizationAsync(AssetDescriptor asset, PaymentPayload payload) =>
        Function(facilitator, asset, "transferWithAuthorization")
            .SendTransactionAndWaitForReceiptAsync(FacilitatorAddress, Gas, NoValue, null, AuthorizationArguments(payload));

    public Task<TransactionReceipt> ReceiptAsync(string txHash) =>
        node.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);

    private static object[] AuthorizationArguments(PaymentPayload payload)
    {
        var exact = payload.AsExactEvm();
        var a = exact.Authorization;
        var signature = Convert.FromHexString(exact.Signature[2..]);

        // Protocol numerics are strings on the wire: parsed here, never sent as JSON numbers.
        return
        [
            a.From, a.To, BigInteger.Parse(a.Value), BigInteger.Parse(a.ValidAfter), BigInteger.Parse(a.ValidBefore),
            Bytes32(a.Nonce), signature[64], signature[..32], signature[32..64],
        ];
    }

    private async Task ImpersonateAsync(string account)
    {
        await node.Client.SendRequestAsync<object>("anvil_impersonateAccount", null, account);
        await node.Client.SendRequestAsync<object>("anvil_setBalance", null, account, "0xDE0B6B3A7640000"); // 1 ETH for gas
    }

    private async Task SendAsImpersonatedAsync(AssetDescriptor asset, string from, string function, params object[] args)
    {
        var receipt = await Function(node, asset, function)
            .SendTransactionAndWaitForReceiptAsync(from, Gas, NoValue, null, args);
        if (receipt.Status.Value != 1)
        {
            throw new InvalidOperationException($"{function} as {from} reverted on the fork (tx {receipt.TransactionHash}).");
        }
    }

    private static Nethereum.Contracts.Function Function(Web3 web3, AssetDescriptor asset, string name)
    {
        asset.Network.ShouldBe(KnownNetworks.BaseSepolia);
        return web3.Eth.GetContract(Abi, asset.Address).GetFunction(name);
    }

    private static byte[] Bytes32(string hex) => Convert.FromHexString(hex.StartsWith("0x", StringComparison.Ordinal) ? hex[2..] : hex);
}
