using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Heyi.AcceptanceTests;

public record NewOrder(Guid Id, bool RejectAfterStoring = false);

// Reads through the request's DbContext, the same instance the endpoint writes with.
public class OrderIsNew(OrdersDbContext db) : IAcceptanceCheck<NewOrder>
{
    public async ValueTask<Acceptance> Check(NewOrder message, HttpContext httpContext) =>
        await db.Orders.AnyAsync(order => order.Id == message.Id, httpContext.RequestAborted)
            ? Acceptance.Reject(TypedResults.UnprocessableEntity())
            : Acceptance.Accept();
}
