namespace NetTrans.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "auto"; // "light" | "dark" | "auto"
    public string Accent { get; set; } = "#0067C0";
    public string Density { get; set; } = "comfy"; // "comfy" | "compact"
    public bool ShowDetailPane { get; set; } = true;
    public bool Throttled { get; set; }

    public string DefaultSavePath { get; set; } = @"C:\Users\you\Downloads";
    public int MaxSimultaneousDownloads { get; set; } = 4;
    public int SegmentsPerFile { get; set; } = 8;

    public bool VerifyChecksums { get; set; } = true;
    public bool AutoExtractArchives { get; set; }
    public bool NotifyOnCompletion { get; set; } = true;
    public bool LaunchAtSignIn { get; set; } = true;
    public bool ResumeInterruptedDownloads { get; set; } = true;

    // Schedule (off-peak window)
    public bool ScheduleEnabled { get; set; } = true;
    public string OffPeakStart { get; set; } = "02:00";
    public string OffPeakEnd { get; set; } = "06:00";

    // Browser capture
    public bool CaptureEdge { get; set; } = true;
    public bool CaptureChrome { get; set; } = true;
    public bool CaptureFirefox { get; set; }
    public bool NotifyOnCapture { get; set; } = true;

    // Advanced
    public int MaxConnectionsPerServer { get; set; } = 16;
    public bool PreallocateFiles { get; set; } = true;
    public string UserAgent { get; set; } = "";
}
