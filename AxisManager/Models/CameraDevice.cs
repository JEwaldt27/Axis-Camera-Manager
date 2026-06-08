namespace AxisManager.Models;

public class CameraDevice
{
    public string Ip        { get; set; } = "";
    public string Name      { get; set; } = "Axis Device";
    public string Mac       { get; set; } = "Unknown";
    public string Status    { get; set; } = "Discovered";
    public bool   Connected { get; set; } = false;
}

public class StreamInfo
{
    public int    Index    { get; set; }
    public string Name     { get; set; } = "";
    public string RtspUrl  { get; set; } = "";
    public string HttpUrl  { get; set; } = "";
}

public class ParamEntry
{
    public string Key   { get; set; } = "";
    public string Value { get; set; } = "";
}
