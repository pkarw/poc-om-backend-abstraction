using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth.Security;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// GET /api/auth/features (auth + <c>auth.acl.manage</c>). Mirrors upstream api/features.ts:
/// aggregate the static feature declarations of every module into
/// <c>{items:[{id,title,module,dependsOn?}], modules:[{id,title}]}</c>. See <see cref="FeatureCatalog"/>
/// for how title/module are derived from the feature id in this port.
/// </summary>
public sealed class FeaturesRoutes : IAuthRouteGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth/features", Handle).RequireFeatures("auth.acl.manage");
    }

    private static IResult Handle(HttpContext http)
    {
        var registry = http.RequestServices.GetRequiredService<ModuleRegistry>();
        var featureIds = registry.Modules.SelectMany(m => m.AclFeatures);
        var moduleInfos = registry.Modules.Select(m => (m.Id, m.Id));
        var (items, modules) = FeatureCatalog.Build(featureIds, moduleInfos);
        return Results.Json(new
        {
            items = items.Select(i => new { id = i.Id, title = i.Title, module = i.Module }),
            modules = modules.Select(m => new { id = m.Id, title = m.Title }),
        });
    }
}
