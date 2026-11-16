using Basi.Core.Models;

namespace Basi.Core.Interfaces;

public interface IAiProvider
{
    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct = default);
}
