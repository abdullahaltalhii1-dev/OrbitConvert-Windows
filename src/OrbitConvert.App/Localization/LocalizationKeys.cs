namespace OrbitConvert.App.Localization;

public static class LocalizationKeys
{
    public const string FileCountNone = nameof(FileCountNone);
    public const string FileCountOne = nameof(FileCountOne);
    public const string FileCountTwo = nameof(FileCountTwo);
    public const string FileCountMany = nameof(FileCountMany);
    public const string IntakeAddedOne = nameof(IntakeAddedOne);
    public const string IntakeAddedTwo = nameof(IntakeAddedTwo);
    public const string IntakeAddedMany = nameof(IntakeAddedMany);
    public const string IntakeDuplicateOne = nameof(IntakeDuplicateOne);
    public const string IntakeDuplicateTwo = nameof(IntakeDuplicateTwo);
    public const string IntakeDuplicateMany = nameof(IntakeDuplicateMany);
    public const string IntakeFolderOne = nameof(IntakeFolderOne);
    public const string IntakeFolderTwo = nameof(IntakeFolderTwo);
    public const string IntakeFolderMany = nameof(IntakeFolderMany);
    public const string IntakeMissingOne = nameof(IntakeMissingOne);
    public const string IntakeMissingTwo = nameof(IntakeMissingTwo);
    public const string IntakeMissingMany = nameof(IntakeMissingMany);
    public const string IntakeInaccessibleOne = nameof(IntakeInaccessibleOne);
    public const string IntakeInaccessibleTwo = nameof(IntakeInaccessibleTwo);
    public const string IntakeInaccessibleMany = nameof(IntakeInaccessibleMany);
    public const string IntakeInvalidOne = nameof(IntakeInvalidOne);
    public const string IntakeInvalidTwo = nameof(IntakeInvalidTwo);
    public const string IntakeInvalidMany = nameof(IntakeInvalidMany);
    public const string IntakeRemoved = nameof(IntakeRemoved);
    public const string IntakeSelectionCleared = nameof(IntakeSelectionCleared);
    public const string IntakeFolderDropRejected = nameof(IntakeFolderDropRejected);

    public static IReadOnlyList<string> RequiredKeys { get; } =
    [
        "HeaderSubtitle", "UiPreview", "ThemeDark", "ThemeLight", "UseDarkTheme", "UseLightTheme",
        "StudioSections", "NavWorkspace", "NavImages", "NavVideo", "NavAudio", "NavDocuments", "NavSettings",
        "NavWorkspaceAutomation", "NavImagesAutomation", "NavVideoAutomation", "NavAudioAutomation",
        "NavDocumentsAutomation", "NavSettingsAutomation", "BadgeDemo", "BadgeSoon", "PhaseLabel", "PhaseDescription",
        "WorkspaceEyebrow", "WorkspaceTitle", "WorkspaceSubtitle", "DropZoneTitle", "DropZoneDescription",
        "BrowseFiles", "BrowseFilesHelp", "SelectedFiles", "ClearAll", "ClearAllHelp", "EmptySelectionTitle",
        "EmptySelectionDescription", "Remove", "RemoveHelp", "PrivateProcessingTitle", "PrivateProcessingDescription",
        "MultipleFormatsTitle", "MultipleFormatsDescription", "FileDialogTitle", "FileDialogFilter",
        FileCountNone, FileCountOne, FileCountTwo, FileCountMany, IntakeAddedOne, IntakeAddedTwo, IntakeAddedMany,
        IntakeDuplicateOne, IntakeDuplicateTwo, IntakeDuplicateMany, IntakeFolderOne, IntakeFolderTwo,
        IntakeFolderMany, IntakeMissingOne, IntakeMissingTwo, IntakeMissingMany, IntakeInaccessibleOne,
        IntakeInaccessibleTwo, IntakeInaccessibleMany, IntakeInvalidOne, IntakeInvalidTwo, IntakeInvalidMany,
        IntakeRemoved, IntakeSelectionCleared, IntakeFolderDropRejected,
        "ImagesEyebrow", "ImagesTitle", "ImagesDescription",
        "Dimensions",
        "ComingSoon",
        "VideoEyebrow", "VideoTitle", "VideoDescription", "VideoFormatsHeading", "VideoInformationalText",
        "AudioEyebrow", "AudioTitle", "AudioDescription", "AudioFormatsHeading", "AudioInformationalText",
        "DocumentsEyebrow", "DocumentsTitle", "DocumentsDescription", "DocumentsFormatsHeading",
        "DocumentsInformationalText", "SettingsEyebrow", "SettingsTitle", "SettingsDescription", "Theme",
        "ThemeDescription", "SettingsDarkAutomation", "SettingsLightAutomation", "AccentColor",
        "AccentDescription", "AccentBlueAutomation", "AccentVioletAutomation", "AccentTealAutomation",
        "ImageSelectSource",
        "ImageSelectionHelp",
        "ImageInspect",
        "ImageSourceFormat",
        "ImageOutputFormat",
        "ImageDestination",
        "ImageChooseDestination",
        "ImageWhiteBackground",
        "ImageFixedSettings",
        "ImageConvert",
        "ImageIdle",
        "ImageInspecting",
        "ImageReady",
        "ImageConverting",
        "ImageSuccess",
        "ImageUnsupportedHelp",
        "ImageCleanupFailed",
        "ImageUnexpectedError",
        "ImageWaitToClose",
        "ImageErrorEmptyInput",
        "ImageErrorUnreadableInput",
        "ImageErrorInvalidOrCorruptImage",
        "ImageErrorUnsupportedFormat",
        "ImageErrorTooLarge",
        "ImageErrorUnsupportedBitDepth",
        "ImageErrorUnsupportedColor",
        "ImageErrorMultipleFrames",
        "ImageErrorResourceLimit",
        "ImageErrorSamePath",
        "ImageErrorDestinationExists",
        "ImageErrorInvalidDestination",
        "ImageErrorOutputFailure",
        "ImageErrorOutputValidationFailed",
        "Language", "LanguageDescription", "LanguageEnglish", "LanguageArabic", "LanguageEnglishAutomation",
        "LanguageArabicAutomation", "SettingsInfo",
    ];
}
