namespace FileOrganizer.Infrastructure.Journaling;

/// <summary>
/// Pins a genuine Windows execution lease while an Infrastructure operation
/// retains physical filesystem handles.
/// </summary>
/// <remarks>
/// This capability is intentionally internal. Callers obtain it only through
/// <see cref="WindowsGlobalExecutionLeaseProvider.TryBorrow"/>; implementing
/// the public Application lease marker is not sufficient.
/// </remarks>
internal interface IWindowsExecutionLeaseBorrow : IDisposable;
