using GridGate.Launcher.Core.Money;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace GridGate.Launcher.Guests;

/// <summary>
/// Makes a resident money page not exist (404) while money is off (money plan 2, task 10). Runs before the antiforgery check,
/// so a post then answers 404 too and nothing is bound or saved.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class MoneyPagesOnlyAttribute : Attribute, IAuthorizationFilter, IOrderedFilter
{
    public int Order => int.MinValue + 1;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!context.HttpContext.RequestServices.GetRequiredService<MoneyGate>().ServiceHere)
            context.Result = new NotFoundResult();
    }
}
