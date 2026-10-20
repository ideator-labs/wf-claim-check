using WfClaimCheck;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace WfClaimCheck
{
    public sealed class ClaimCheckOptions
    {
        /// <summary>Workflow data whose JSON is larger than this goes to the payload store.</summary>
        public int ThresholdBytes { get; set; } = 64 * 1024;
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    public static class ClaimCheckWorkflowOptionsExtensions
    {
        /// <summary>
        /// Use <paramref name="inner"/> as the persistence provider, with large workflow data
        /// offloaded to <paramref name="store"/>.
        /// </summary>
        public static WorkflowOptions UseClaimCheck(
            this WorkflowOptions options,
            Func<IServiceProvider, IPersistenceProvider> inner,
            IPayloadStore store,
            Action<ClaimCheckOptions>? configure = null)
        {
            var claimOptions = new ClaimCheckOptions();
            configure?.Invoke(claimOptions);
            options.UsePersistence(sp => new ClaimCheckPersistenceProvider(inner(sp), store, claimOptions));
            return options;
        }
    }
}
