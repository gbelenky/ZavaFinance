using ZavaFinance.Agent;
using ZavaFinance.Core.Identity;

namespace ZavaFinance.One;

public interface IActivityAssertionValidator
{
    Task<CallerIdentity> ValidateAsync(string? assertion, CancellationToken cancellationToken);
}

public sealed class ActivityAssertionValidator(UserAssertionValidator validator) : IActivityAssertionValidator
{
    public async Task<CallerIdentity> ValidateAsync(string? assertion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            CallerIdentity caller = await validator.ValidateAsync(assertion, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return caller;
        }
        catch (CallerIdentityException) when (cancellationToken.IsCancellationRequested)
        {
            // The existing validator classifies metadata retrieval cancellation as identity failure.
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }
}
