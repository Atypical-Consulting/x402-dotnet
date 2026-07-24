namespace X402.TestKit;

/// <summary>How the fake facilitator should behave for the next calls.</summary>
public enum FakeFacilitatorScenario
{
    /// <summary>Verify the signature for real and settle successfully.</summary>
    Valid,

    /// <summary>Reject verification with <c>insufficient_funds</c>.</summary>
    InsufficientFunds,

    /// <summary>Reject any asset outside <c>SupportedAssets</c>, as a USDC-only facilitator would.</summary>
    UnsupportedAsset,

    /// <summary>Abort the connection without answering.</summary>
    NetworkFailure,

    /// <summary>Answer after a delay longer than the caller's timeout.</summary>
    Timeout,

    /// <summary>Verify successfully but fail settlement.</summary>
    SettleFailure,

    /// <summary>Answer with a bare 500, as if the facilitator suffered an internal failure.</summary>
    ServerError,
}
