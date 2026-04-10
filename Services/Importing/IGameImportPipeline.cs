using Codec.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Services.Importing
{
    public interface IGameImportPipeline
    {
        Task<GameImportResult> ImportAsync(GameImportRequest request, IReadOnlyCollection<Game> librarySnapshot, CancellationToken cancellationToken = default);
    }
}
