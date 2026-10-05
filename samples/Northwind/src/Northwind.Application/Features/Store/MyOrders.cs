using Can.Core.Application;
using Can.Core.Application.Exceptions;
using Can.Core.Mediator;
using Can.Core.Persistence.Paging;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Northwind.Application.Common;
using Northwind.Application.Features.Orders;
using Northwind.Domain.Customers;
using Northwind.Domain.Orders;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Store;

// Siteden kayıt olan müşterinin işlemleri. Müşteri, giriş yapan kullanıcıdan bulunur; istemci müşteri
// kimliği GÖNDEREMEZ (başkasının adına sipariş veremez, başkasının siparişini göremez).

/// <summary>Giriş yapmış kullanıcıya bağlı müşteri kaydı.</summary>
public sealed class CurrentCustomer
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<Customer, Guid> _customers;

    public CurrentCustomer(ICurrentUser currentUser, IRepository<Customer, Guid> customers)
    {
        _currentUser = currentUser;
        _customers = customers;
    }

    public async Task<Customer> GetAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(_currentUser.Id, out Guid userId))
            throw new UnauthorizedException();

        return await _customers.GetAsync(c => c.UserId == userId, enableTracking: false, cancellationToken: cancellationToken)
            ?? throw new ForbiddenException("Hesabına bağlı bir müşteri kaydı yok.") { Code = "customer_required" };
    }
}

public sealed record CheckoutLine(Guid ProductId, int Quantity);

/// <summary>Sepeti siparişe çevirir. Kargo: 500'ün üzerindeki siparişlerde ücretsiz, altında 15.</summary>
public sealed record CheckoutCommand(IReadOnlyList<CheckoutLine> Lines, ShipToDto? ShipTo)
    : IRequest<PlaceOrderResult>, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
{
    public const decimal FreeShippingThreshold = 500m;
    public const decimal ShippingFee = 15m;

    public IReadOnlyCollection<string> Roles => [AppRoles.Customer];
    public IReadOnlyCollection<string> CacheTagsToRemove => [OrderCacheTags.Reports];
}

public sealed class CheckoutCommandValidator : AbstractValidator<CheckoutCommand>
{
    public CheckoutCommandValidator()
    {
        RuleFor(c => c.Lines).NotEmpty().WithMessage("Sepet boş.").Must(l => l.Count <= 50).WithMessage("Sepette en fazla 50 ürün olabilir.");
        RuleForEach(c => c.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId).NotEmpty();
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, 1000);
        });
        RuleFor(c => c.ShipTo!.Name).NotEmpty().MaximumLength(Order.ShipNameMaxLength).When(c => c.ShipTo is not null);
        RuleFor(c => c.ShipTo)
            .Must(s => ValidationExtensions.IsComplete(s!.Address))
            .WithMessage(ValidationExtensions.AddressMessage)
            .When(c => c.ShipTo is not null);
    }
}

public sealed record GetMyOrdersQuery(PageRequest Page) : IRequest<IPaginate<OrderListItemDto>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Customer];
}

public sealed record GetMyOrderQuery(Guid Id) : IRequest<OrderDto>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Customer];
}

public sealed record CancelMyOrderCommand(Guid Id) : IRequest, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Customer];
    public IReadOnlyCollection<string> CacheTagsToRemove => [OrderCacheTags.Reports];
}

public sealed class MyOrderHandlers
    : IRequestHandler<CheckoutCommand, PlaceOrderResult>,
        IRequestHandler<GetMyOrdersQuery, IPaginate<OrderListItemDto>>,
        IRequestHandler<GetMyOrderQuery, OrderDto>,
        IRequestHandler<CancelMyOrderCommand>
{
    private readonly CurrentCustomer _currentCustomer;
    private readonly OrderPlacer _placer;
    private readonly OrderDetails _details;
    private readonly IRepository<Order, Guid> _orders;

    public MyOrderHandlers(CurrentCustomer currentCustomer, OrderPlacer placer, OrderDetails details, IRepository<Order, Guid> orders)
    {
        _currentCustomer = currentCustomer;
        _placer = placer;
        _details = details;
        _orders = orders;
    }

    public async Task<PlaceOrderResult> Handle(CheckoutCommand request, CancellationToken cancellationToken)
    {
        Customer customer = await _currentCustomer.GetAsync(cancellationToken);

        Order order = await _placer.PlaceAsync(
            customer,
            employeeId: null,
            requiredDate: null,
            freight: subtotal => subtotal >= CheckoutCommand.FreeShippingThreshold ? 0 : CheckoutCommand.ShippingFee,
            request.ShipTo,
            request.Lines.Select(l => new PlaceOrderLine(l.ProductId, l.Quantity)).ToList(),
            cancellationToken
        );

        return new PlaceOrderResult(order.Id, order.Number, order.Total);
    }

    public async Task<IPaginate<OrderListItemDto>> Handle(GetMyOrdersQuery request, CancellationToken cancellationToken)
    {
        Customer customer = await _currentCustomer.GetAsync(cancellationToken);

        return await _orders
            .Query(enableTracking: false)
            .Where(o => o.CustomerId == customer.Id)
            .OrderByDescending(o => o.Number)
            .Select(o => new OrderListItemDto(
                o.Id,
                o.Number,
                o.CustomerId,
                customer.CompanyName,
                o.Status,
                o.OrderedAt,
                o.ShippedAt,
                o.Lines.Sum(l => l.UnitPrice * l.Quantity * (1 - l.Discount)) + o.Freight
            ))
            .ToPaginateAsync(request.Page.Index, request.Page.Size, cancellationToken: cancellationToken);
    }

    public async Task<OrderDto> Handle(GetMyOrderQuery request, CancellationToken cancellationToken)
    {
        Customer customer = await _currentCustomer.GetAsync(cancellationToken);
        return await _details.GetAsync(request.Id, customer.Id, cancellationToken);
    }

    public async Task Handle(CancelMyOrderCommand request, CancellationToken cancellationToken)
    {
        Customer customer = await _currentCustomer.GetAsync(cancellationToken);

        Order order =
            await _orders.GetAsync(o => o.Id == request.Id && o.CustomerId == customer.Id, include: q => q.Include(o => o.Lines), cancellationToken: cancellationToken)
            ?? throw NotFoundException.For<Order>(request.Id);

        order.Cancel();
    }
}
