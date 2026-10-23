using System.ComponentModel;
using System.IO;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Services.Batch;

namespace OrbitConvert.App.ViewModels;

public sealed class BatchJob : INotifyPropertyChanged
{
    private readonly LocalizationService _localization;
    internal BatchJob(BatchRequest request, LocalizationService localization) { Request = request; _localization = localization; }
    public Guid Id { get; } = Guid.NewGuid();
    public BatchRequest Request { get; internal set; }
    public string FileName => Path.GetFileName(Request.Source);
    public string FileType => Path.GetExtension(Request.Source);
    public string PlannedName => Path.GetFileName(Request.Destination);
    public string Destination => Request.Destination;
    public string ModeLabel => _localization["BatchMode" + Request.Preset.Key];
    public BatchState State { get; internal set; } = BatchState.Pending;
    public string StateLabel => _localization["BatchState" + State];
    public string? ErrorKey { get; internal set; }
    public string ErrorText => (ErrorKey is null ? "" : _localization[ErrorKey]) + (CleanupWarning ? "\n" + _localization["BatchCleanupWarning"] : "");
    public string ResultPath { get; internal set; } = "";
    public bool CancellationRequested { get; internal set; }
    public bool CleanupWarning { get; internal set; }
    public double? Fraction { get; internal set; }
    public double ProgressPercent => Math.Clamp(Fraction ?? 0, 0, 1) * 100;
    public bool IsIndeterminate => IsActive && Fraction is null;
    public bool IsActive => State is BatchState.Inspecting or BatchState.Running or BatchState.Verifying;
    public bool CanCancel => (State == BatchState.Pending || IsActive) && !CancellationRequested;
    public string CancellationText => CancellationRequested && IsActive ? _localization["BatchCancellationRequested"] : "";
    internal int Attempt { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new(string.Empty));
}
