namespace AirStereo2.Services;

public interface IRegistryRunStore
{
	string? GetValue(string valueName);

	void SetValue(string valueName, string commandLine);

	void DeleteValue(string valueName);
}
