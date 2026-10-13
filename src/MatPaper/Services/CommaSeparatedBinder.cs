using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;

namespace MatPaper.Services;

/// <summary>
/// Binds a list of numbers (<c>long[]</c>, <c>List&lt;long&gt;</c>, <c>int[]</c> ...) from what the toolkit's multiple picker posts: ONE value with the
/// keys joined by a comma (<c>1,4,7</c>). Values posted the usual way - one per key, as a group of checkboxes does - are read as before, so the same
/// property takes both. Nothing posted leaves the property as it is.
/// </summary>
public sealed class CommaSeparatedBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        var type = context.Metadata.ModelType;
        var element = type.IsArray ? type.GetElementType() : type.IsGenericType && type.GetGenericArguments().Length == 1 ? type.GetGenericArguments()[0] : null;
        if (element is null || (element != typeof(long) && element != typeof(int)))
        {
            return null;
        }

        bool supported = type.IsArray
            || type == typeof(List<>).MakeGenericType(element)
            || type == typeof(IList<>).MakeGenericType(element)
            || type == typeof(ICollection<>).MakeGenericType(element)
            || type == typeof(IEnumerable<>).MakeGenericType(element)
            || type == typeof(IReadOnlyList<>).MakeGenericType(element)
            || type == typeof(IReadOnlyCollection<>).MakeGenericType(element);
        if (!supported)
        {
            return null;
        }

        return element == typeof(long) ? new CommaSeparatedBinder<long>(type) : new CommaSeparatedBinder<int>(type);
    }
}

internal sealed class CommaSeparatedBinder<T>(Type modelType) : IModelBinder
    where T : struct, IParsable<T>
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var result = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (result == ValueProviderResult.None)
        {
            return Task.CompletedTask;
        }

        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, result);
        var list = new List<T>();
        foreach (var raw in result.Values)
        {
            foreach (var part in (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!T.TryParse(part, CultureInfo.InvariantCulture, out var value))
                {
                    bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, $"The value '{part}' is not valid.");
                    bindingContext.Result = ModelBindingResult.Failed();
                    return Task.CompletedTask;
                }

                list.Add(value);
            }
        }

        bindingContext.Result = ModelBindingResult.Success(modelType.IsArray ? list.ToArray() : list);
        return Task.CompletedTask;
    }
}
