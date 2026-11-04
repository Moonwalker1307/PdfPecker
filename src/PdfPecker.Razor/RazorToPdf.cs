using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PdfPecker.Razor;

/// <summary>
/// Converts Razor components (<c>.razor</c> files) to PDF. A component is rendered to HTML with Microsoft's
/// <see cref="HtmlRenderer"/>, as ASP.NET Core renders a page on the server, and the HTML is converted with
/// <see cref="HtmlToPdf"/>. No ASP.NET Core host, browser or JavaScript is involved.
/// </summary>
/// <remarks>
/// <para>
/// Razor encodes every value it writes into the markup, so caller data such as names and amounts cannot become markup;
/// only <see cref="MarkupString"/> inserts HTML as it is. The component renders once, static and without
/// interactivity: event handlers never run, and asynchronous work in <c>OnInitializedAsync</c> or
/// <c>OnParametersSetAsync</c> is awaited before the HTML is taken.
/// </para>
/// <para>
/// All methods are static and thread-safe. Each call renders in a renderer of its own, which is disposed with the
/// component when the call returns, so nothing is kept between conversions.
/// </para>
/// </remarks>
public static class RazorToPdf
{
    // A component's type is annotated as Blazor annotates it, so that trimming keeps its constructor and the
    // [Parameter] and [Inject] properties the renderer sets.
    private const DynamicallyAccessedMemberTypes Component = DynamicallyAccessedMemberTypes.All;

    // Services for components when the caller gives none. It holds no registrations, so a component that injects a
    // service fails with the renderer's own message naming that service.
    private static readonly Lazy<ServiceProvider> NoServices = new(() => new ServiceCollection().BuildServiceProvider());

    /// <summary>Renders <typeparamref name="TComponent"/> to HTML and converts it to a PDF document.</summary>
    /// <typeparam name="TComponent">The component, usually a <c>.razor</c> file.</typeparam>
    /// <param name="parameters">
    /// The component's parameters by property name, such as <c>["Invoice"] = invoice</c>; null for none.
    /// </param>
    /// <param name="options">Page, font and output settings; null for the defaults.</param>
    /// <param name="services">
    /// The services that <c>@inject</c> and constructor injection take from, such as an ASP.NET Core app's
    /// <c>RequestServices</c>; null for none. An <see cref="ILoggerFactory"/> among them receives the renderer's log.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops the conversion. The component's own rendering is not interrupted; pass the token to it as a parameter when
    /// it does slow work of its own.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The renderer refused the component, for example because a parameter has no matching <c>[Parameter]</c>
    /// property or an injected service is not registered.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> holds a value out of range or a combination that is not allowed, as for
    /// <see cref="HtmlToPdf.ConvertAsync(string, PdfOptions?, CancellationToken)"/>.
    /// </exception>
    /// <exception cref="PdfLimitException">The document would have more than <see cref="PdfOptions.MaxPages"/> pages.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>An exception that the component itself throws while rendering reaches the caller unchanged.</remarks>
    public static Task<byte[]> ConvertAsync<[DynamicallyAccessedMembers(Component)] TComponent>(
        IDictionary<string, object?>? parameters = null,
        PdfOptions? options = null,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
        where TComponent : IComponent
        => ConvertAsync(typeof(TComponent), parameters, options, services, cancellationToken);

    /// <summary>Renders the component of type <paramref name="componentType"/> to HTML and converts it to a PDF document.</summary>
    /// <param name="componentType">The component's type, which implements <see cref="IComponent"/>.</param>
    /// <param name="parameters">The component's parameters by property name; null for none.</param>
    /// <param name="options">Page, font and output settings; null for the defaults.</param>
    /// <param name="services">The services the component injects; null for none.</param>
    /// <param name="cancellationToken">Stops the conversion, though not the component's own rendering.</param>
    /// <exception cref="ArgumentNullException"><paramref name="componentType"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="componentType"/> is not a component, or <paramref name="options"/> holds a value that is not allowed.
    /// </exception>
    /// <exception cref="InvalidOperationException">The renderer refused the component or one of its parameters.</exception>
    /// <exception cref="PdfLimitException">The document would have more than <see cref="PdfOptions.MaxPages"/> pages.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<byte[]> ConvertAsync(
        [DynamicallyAccessedMembers(Component)] Type componentType,
        IDictionary<string, object?>? parameters = null,
        PdfOptions? options = null,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
    {
        string html = await RenderHtmlAsync(componentType, parameters, services, cancellationToken).ConfigureAwait(false);
        return await HtmlToPdf.ConvertAsync(html, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders <typeparamref name="TComponent"/>, converts it and writes the PDF to <paramref name="output"/>
    /// asynchronously, as ASP.NET Core requires of a response body. Nothing is written unless the conversion completes.
    /// </summary>
    /// <param name="output">Where the PDF is written; it is not flushed or closed.</param>
    /// <param name="parameters">The component's parameters by property name; null for none.</param>
    /// <param name="options">Page, font and output settings; null for the defaults.</param>
    /// <param name="services">The services the component injects; null for none.</param>
    /// <param name="cancellationToken">Stops the conversion and the write, though not the component's own rendering.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The renderer refused the component or one of its parameters.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task ConvertAsync<[DynamicallyAccessedMembers(Component)] TComponent>(
        Stream output,
        IDictionary<string, object?>? parameters = null,
        PdfOptions? options = null,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
        where TComponent : IComponent
    {
        ArgumentNullException.ThrowIfNull(output);
        var bytes = await ConvertAsync(typeof(TComponent), parameters, options, services, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders <typeparamref name="TComponent"/> to the HTML that <see cref="ConvertAsync{TComponent}(IDictionary{string, object?}?, PdfOptions?, IServiceProvider?, CancellationToken)"/>
    /// converts, to look at in a browser or to cache.
    /// </summary>
    /// <param name="parameters">The component's parameters by property name; null for none.</param>
    /// <param name="services">The services the component injects; null for none.</param>
    /// <param name="cancellationToken">Checked before rendering starts; the component's own rendering is not interrupted.</param>
    /// <exception cref="InvalidOperationException">The renderer refused the component or one of its parameters.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Task<string> RenderHtmlAsync<[DynamicallyAccessedMembers(Component)] TComponent>(
        IDictionary<string, object?>? parameters = null,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
        where TComponent : IComponent
        => RenderHtmlAsync(typeof(TComponent), parameters, services, cancellationToken);

    /// <summary>Renders the component of type <paramref name="componentType"/> to HTML.</summary>
    /// <param name="componentType">The component's type, which implements <see cref="IComponent"/>.</param>
    /// <param name="parameters">The component's parameters by property name; null for none.</param>
    /// <param name="services">The services the component injects; null for none.</param>
    /// <param name="cancellationToken">Checked before rendering starts; the component's own rendering is not interrupted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="componentType"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="componentType"/> is not a component.</exception>
    /// <exception cref="InvalidOperationException">The renderer refused the component or one of its parameters.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<string> RenderHtmlAsync(
        [DynamicallyAccessedMembers(Component)] Type componentType,
        IDictionary<string, object?>? parameters = null,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        if (!typeof(IComponent).IsAssignableFrom(componentType))
            throw new ArgumentException($"{componentType.FullName} does not implement {nameof(IComponent)}.", nameof(componentType));
        cancellationToken.ThrowIfCancellationRequested();

        services ??= NoServices.Value;
        var loggerFactory = services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        var parameterView = parameters == null ? ParameterView.Empty : ParameterView.FromDictionary(parameters);

        // The renderer lives for this one component: HtmlRenderer keeps every root component it has rendered until it
        // is disposed, so sharing one between conversions would keep every document's components alive.
        await using var renderer = new HtmlRenderer(services, loggerFactory);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            // The HTML is read on the renderer's dispatcher too, so the await stays on its synchronisation context.
            var root = await renderer.RenderComponentAsync(componentType, parameterView);
            return root.ToHtmlString();
        }).ConfigureAwait(false);
    }
}
