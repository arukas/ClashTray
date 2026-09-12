using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class MihomoConfigurationCandidateValidator : IConfigurationCandidateValidator
{
    private readonly AppPaths _paths;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly Func<string?> _executableProvider;

    public MihomoConfigurationCandidateValidator(
        AppPaths paths,
        Func<AppSettings> settingsProvider,
        Func<string?> executableProvider)
    {
        _paths = paths;
        _settingsProvider = settingsProvider;
        _executableProvider = executableProvider;
    }

    public async Task ValidateAsync(string candidatePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidatePath);

        string? executablePath = _executableProvider();
        if (executablePath is null)
        {
            return;
        }

        await ManagedCoreVerifier.ValidateAsync(_paths, cancellationToken);
        string runtimeDirectory = Path.Combine(_paths.RuntimeRoot, "mihomo");
        string runtimeCandidatePath = Path.Combine(
            runtimeDirectory,
            $"candidate-config-{Guid.NewGuid():N}.yaml");
        try
        {
            await RuntimeConfigBuilder.BuildAsync(
                candidatePath,
                runtimeCandidatePath,
                _settingsProvider(),
                cancellationToken);

            await using MihomoProcessManager validator = new MihomoProcessManager();
            bool valid = await validator.ValidateAsync(
                executablePath,
                runtimeCandidatePath,
                runtimeDirectory,
                cancellationToken);
            if (!valid)
            {
                throw new InvalidDataException("Mihomo 拒绝了候选配置。");
            }
        }
        finally
        {
            if (File.Exists(runtimeCandidatePath))
            {
                File.Delete(runtimeCandidatePath);
            }
        }
    }
}
