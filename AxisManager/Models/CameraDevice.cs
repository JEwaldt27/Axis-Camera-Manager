namespace AxisManager.Models;

public enum CameraSetupState
{
    Unknown,        // Not yet probed
    NeedsSetup,     // Factory default — no password set, setup wizard required
    NeedsAuth,      // Has a password but we don't know it
    Ready           // Connected and authenticated
}

public class CameraDevice
{
    public string           Ip         { get; set; } = "";
    public string           Name       { get; set; } = "Axis Device";
    public string           Mac        { get; set; } = "Unknown";
    public string           Status     { get; set; } = "Discovered";
    public bool             Connected  { get; set; } = false;
    public CameraSetupState SetupState { get; set; } = CameraSetupState.Unknown;
}

public class StreamInfo
{
    public int    Index   { get; set; }
    public string Name    { get; set; } = "";
    public string RtspUrl { get; set; } = "";
    public string HttpUrl { get; set; } = "";
}

public class ParamEntry
{
    public string Key   { get; set; } = "";
    public string Value { get; set; } = "";
}
