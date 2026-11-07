namespace ManagedLauncher.Core.Config;

public enum TargetKind { Lnk, Aumid, Glob, Exe, Url }

public enum OnRunning { Auto, Focus, Relaunch, None }

public enum KillMode { All, Newest }

public enum IndicatorStyle { Border, Dot }

public enum LongPressMode { Auto, None }

public enum ListBreak { Row, Page }

/// <summary>Static lists hold the items the user put in; dynamic lists are built from <see cref="ListSource"/>.</summary>
public enum ListKind { Static, Dynamic }

/// <summary>Where an auto-folder takes its items from.</summary>
public enum SourceType { Steam, Directory, StartMenu, Running }

/// <summary>How the launcher turns pages: one wrapping next-page key, or previous and next arrows on the last row.</summary>
public enum Navigation { Carousel, Arrows }

/// <summary>Colour of the tiles behind control and folder keys.</summary>
public enum KeyTheme { Grey, Blue, Red }
