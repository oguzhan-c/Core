using CanFx.Mediator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.WebApi.Controllers;

/// <summary>
/// Controller'ların tabanı (nArchitecture'daki gibi): komut/sorgular <see cref="Mediator"/> ile gönderilir. Hatalar
/// exception olarak fırlar ve <c>UseCanExceptionHandler</c> ProblemDetails'e çevirir.
/// </summary>
/// <example>
/// <code>
/// [Route("api/v1/[controller]")]
/// [ApiController]
/// public class BrandsController : BaseController
/// {
///     [HttpGet("{Id}")]
///     public async Task&lt;IActionResult&gt; GetById([FromRoute] GetByIdBrandQuery query) =&gt; Ok(await Mediator.Send(query));
/// }
/// </code>
/// </example>
public abstract class BaseController : ControllerBase
{
    private ISender? _mediator;

    /// <summary>İstek kapsamındaki gönderici.</summary>
    protected ISender Mediator => _mediator ??= HttpContext.RequestServices.GetRequiredService<ISender>();
}
