namespace ClipPort.Models;

/// <summary>
/// Carries a launch request across process and package-identity boundaries.
/// A null quick-start request means that the existing window should only be activated.
/// </summary>
public sealed record AppActivationRequest(QuickStartRequest? QuickStartRequest);
