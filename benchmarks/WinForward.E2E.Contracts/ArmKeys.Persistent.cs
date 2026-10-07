namespace WinForward.E2E.Contracts;

/// <summary>
/// The <c>metrics</c> object of a <c>persistent</c> result record: one long-lived connection's paced
/// exchanges, the reconnect and idle-window evidence around them, and the error counters that explain
/// a request the arm could not complete.
/// </summary>
/// <remarks>
/// <para><b>Conditional fields: none.</b> Every key here is published by every run. A reading with no
/// measurement behind it is written as JSON <see langword="null"/> and keeps its key, so
/// <c>meanConnectMs</c> is null when no connect completed rather than a zero that would read as an
/// instantaneous one.</para>
/// <para>The record's <c>gates</c> keys are shared and live in <see cref="ArmKeys.Common.Gates"/>; its
/// <c>parameters</c> keys in <see cref="ArmKeys.Common.Parameters"/>.</para>
/// </remarks>
public static partial class ArmKeys
{
    /// <summary>Keys under <c>metrics</c> for the <c>persistent</c> kind.</summary>
    public static class Persistent
    {
        /// <summary>Paced exchanges the arm attempted.</summary>
        public const string Requests = "requests";

        /// <summary>Exchanges an echo completed.</summary>
        public const string Responses = "responses";

        /// <summary>Requests that had to open a replacement connection first.</summary>
        public const string Reconnects = "reconnects";

        /// <summary>Whether the connection live when the idle window opened completed the first request after it.</summary>
        public const string SurvivedIdle = "survivedIdle";

        /// <summary>Whole pacing intervals the requested idle period was rounded to.</summary>
        public const string IdleSecondsScheduled = "idleSecondsScheduled";

        /// <summary>Silence observed between the last request before the window and the first after it.</summary>
        public const string IdleSecondsObserved = "idleSecondsObserved";

        /// <summary>Sends the kernel did not accept synchronously; those sends still complete.</summary>
        public const string SendWouldBlock = "sendWouldBlock";

        /// <summary>Sends that threw.</summary>
        public const string SendFailures = "sendFailures";

        /// <summary>Rounds whose echo did not arrive inside the response timeout.</summary>
        public const string Timeouts = "timeouts";

        /// <summary>Rounds whose connection was closed or faulted while the arm waited.</summary>
        public const string RemoteClosed = "remoteClosed";

        /// <summary>Frames that failed their checksum, and framing errors.</summary>
        public const string ProtocolErrors = "protocolErrors";

        /// <summary>Frames whose payload did not match its filler.</summary>
        public const string Corrupt = "corrupt";

        /// <summary>Valid frames that matched no outstanding request.</summary>
        public const string UnmatchedReplies = "unmatchedReplies";

        /// <summary>Connects the arm started.</summary>
        public const string ConnectAttempts = "connectAttempts";

        /// <summary>Connects in that same population that failed.</summary>
        public const string ConnectFailures = "connectFailures";

        /// <summary>Mean connect duration in milliseconds; null when none succeeded.</summary>
        public const string MeanConnectMs = "meanConnectMs";

        /// <summary>Responses over requests; null when nothing was offered.</summary>
        public const string ResponseRate = "responseRate";

        /// <summary>Requests whose send completed per elapsed second; null when no time passed.</summary>
        public const string AchievedRate = "achievedRate";

        /// <summary>Responses per elapsed second; null when no time passed.</summary>
        public const string CompletionRate = "completionRate";
    }
}
