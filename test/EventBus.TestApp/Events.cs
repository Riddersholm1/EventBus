namespace EventBus.TestApp;

public sealed record MessageSent(string Text, DateTime Timestamp);
public sealed record CounterChanged(int NewValue);
public sealed record ThemeChanged(string CssClass, string Label);
public sealed record NotificationPosted(string Message, string Level = "info");