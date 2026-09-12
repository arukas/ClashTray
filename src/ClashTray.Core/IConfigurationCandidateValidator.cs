namespace ClashTray.Core;

public interface IConfigurationCandidateValidator
{
    public Task ValidateAsync(string candidatePath, CancellationToken cancellationToken = default);
}
