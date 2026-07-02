using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cairn.AspNetCore;

/// <summary>
/// Projects hypermedia links and affordances onto an MVC action's (or controller's) responses — the
/// controller counterpart to <c>WithLinks()</c>. The returned value, and each element of a returned
/// collection, is linked according to its runtime type's configuration.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class CairnLinksAttribute : Attribute, IAsyncResultFilter
{
    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // Record before the result executes so the payload is stashed when the formatter serializes the value.
        // A deferred sequence comes back as its buffered copy — serialize that so links stay correlated by
        // reference (and the sequence isn't enumerated a second time).
        if (context.Result is ObjectResult { Value: { } value } objectResult)
        {
            var recorded = await CairnLinkRecorder.RecordValueAsync(context.HttpContext, value);
            if (!ReferenceEquals(recorded, value))
            {
                objectResult.Value = recorded;
            }
        }

        await next();
    }
}
