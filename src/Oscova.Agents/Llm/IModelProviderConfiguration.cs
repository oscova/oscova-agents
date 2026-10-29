using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Llm;

/// <summary>Lets a provider validate its options and retire resources for one definition.</summary>
public interface IModelProviderConfiguration
{
    /// <summary>Returns field errors without loading native code, accessing the network or writing data.</summary>
    IReadOnlyDictionary<string, string> ValidateConfiguration(ModelProviderConfig config);

    /// <summary>Releases one definition's resources after its admitted executions have drained.</summary>
    Task ReleaseConfigurationAsync(Guid providerId, CancellationToken cancellationToken = default);
}
