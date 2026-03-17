namespace NET.Deploy.Api.Logic.Deploy;

public delegate Task LogCallback(string level, string message, string? serviceId = null);
