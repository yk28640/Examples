namespace AutomationPlatform;

public static class DiscoveryHistoryApi
{
    public static RouteGroupBuilder MapDiscoveryHistoryApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/discovery/scans");

        group.MapGet("", async (int? page, int? pageSize, DiscoveryHistoryService history,
            CancellationToken cancellationToken) =>
        {
            var requestedPage = page ?? 1;
            var requestedPageSize = pageSize ?? 20;
            if (requestedPage < 1 || requestedPageSize is < 1 or > 100)
                return Results.BadRequest("page 必须大于 0；pageSize 必须介于 1 和 100 之间。");

            var result = await Task.Run(() => history.GetScans(requestedPage, requestedPageSize, cancellationToken),
                cancellationToken);
            return Results.Ok(result);
        });

        group.MapGet("/{id:long}", async (long id, DiscoveryHistoryService history,
            CancellationToken cancellationToken) =>
        {
            if (id < 1)
                return Results.BadRequest("id 必须大于 0。");

            var result = await Task.Run(() => history.GetScan(id, cancellationToken), cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        return group;
    }
}
