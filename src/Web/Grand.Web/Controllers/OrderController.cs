﻿using Grand.Business.Core.Commands.Checkout.Orders;
using Grand.Business.Core.Interfaces.Checkout.Orders;
using Grand.Business.Core.Interfaces.Checkout.Payments;
using Grand.Business.Core.Interfaces.Checkout.Shipping;
using Grand.Business.Core.Interfaces.Common.Directory;
using Grand.Business.Core.Interfaces.Common.Localization;
using Grand.Business.Core.Interfaces.Common.Pdf;
using Grand.Domain.Orders;
using Grand.Domain.Payments;
using Grand.Domain.Shipping;
using Grand.Infrastructure;
using Grand.Web.Commands.Models.Orders;
using Grand.Web.Common.Controllers;
using Grand.Web.Common.Filters;
using Grand.Web.Common.Page;
using Grand.Web.Events;
using Grand.Web.Extensions;
using Grand.Web.Features.Models.Orders;
using Grand.Web.Models.Orders;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Grand.Web.Common.Security.Authorization;
using Grand.Domain.Customers;

namespace Grand.Web.Controllers
{
    [DenySystemAccount]
    public class OrderController : BasePublicController
    {
        #region Fields

        private readonly IOrderService _orderService;
        private readonly IWorkContext _workContext;
        private readonly IPaymentService _paymentService;
        private readonly IPaymentTransactionService _paymentTransactionService;
        private readonly ITranslationService _translationService;
        private readonly IGroupService _groupService;
        private readonly IMediator _mediator;
        private readonly OrderSettings _orderSettings;

        #endregion

        #region Constructors

        public OrderController(IOrderService orderService,
            IWorkContext workContext,
            IPaymentService paymentService,
            IPaymentTransactionService paymentTransactionService,
            ITranslationService translationService,
            IGroupService groupService,
            IMediator mediator,
            OrderSettings orderSettings)
        {
            _orderService = orderService;
            _workContext = workContext;
            _paymentService = paymentService;
            _paymentTransactionService = paymentTransactionService;
            _translationService = translationService;
            _groupService = groupService;
            _mediator = mediator;
            _orderSettings = orderSettings;
        }

        #endregion

        #region Utilities

        protected virtual bool IsRequestBeingRedirected {
            get {
                var response = HttpContext.Response;
                return new List<int> { 301, 302 }.Contains(response.StatusCode);
            }
        }
        protected virtual bool IsPostBeingDone {
            get => HttpContext.Items["grand.IsPOSTBeingDone"] != null && Convert.ToBoolean(HttpContext.Items["grand.IsPOSTBeingDone"]);
            set => HttpContext.Items["grand.IsPOSTBeingDone"] = value;
        }


        #endregion

        #region Methods

        //My account / Orders
        [HttpGet]
        [CustomerGroupAuthorize(SystemCustomerGroupNames.Registered)]
        public virtual async Task<IActionResult> CustomerOrders(OrderPagingModel command)
        {
            var model = await _mediator.Send(new GetCustomerOrderList {
                Customer = _workContext.CurrentCustomer,
                Language = _workContext.WorkingLanguage,
                Store = _workContext.CurrentStore,
                Command = command
            });
            return View(model);
        }

        //My account / Order details page
        [HttpGet]
        public virtual async Task<IActionResult> Details(string orderId)
        {
            var order = await _orderService.GetOrderById(orderId);
            if (!await order.Access(_workContext.CurrentCustomer, _groupService))
                return Challenge();

            var model = await _mediator.Send(new GetOrderDetails { Order = order, Language = _workContext.WorkingLanguage });

            return View(model);
        }
        //My account / Order details page / Cancel Unpaid Order
        [HttpGet]
        public virtual async Task<IActionResult> CancelOrder(string orderId)
        {
            var order = await _orderService.GetOrderById(orderId);
            if (!await order.Access(_workContext.CurrentCustomer, _groupService)
                || order.PaymentStatusId != PaymentStatus.Pending
                || (order.ShippingStatusId != ShippingStatus.ShippingNotRequired && order.ShippingStatusId != ShippingStatus.Pending)
                || order.OrderStatusId != (int)OrderStatusSystem.Pending
                || !_orderSettings.UserCanCancelUnpaidOrder)

                return Challenge();

            await _mediator.Send(new CancelOrderCommand { Order = order, NotifyCustomer = true, NotifyStoreOwner = true });

            return RedirectToRoute("OrderDetails", new { orderId });
        }

        //My account / Order details page / PDF invoice
        [HttpGet]
        public virtual async Task<IActionResult> GetPdfInvoice(string orderId, [FromServices] IPdfService pdfService)
        {
            var order = await _orderService.GetOrderById(orderId);
            if (!await order.Access(_workContext.CurrentCustomer, _groupService))
                return Challenge();

            var orders = new List<Order> { order };
            byte[] bytes;
            using (var stream = new MemoryStream())
            {
                await pdfService.PrintOrdersToPdf(stream, orders, _workContext.WorkingLanguage.Id);
                bytes = stream.ToArray();
            }
            return File(bytes, "application/pdf", $"order_{order.Id}.pdf");
        }

        //My account / Order details page / Add order note        
        [HttpPost]
        [AutoValidateAntiforgeryToken]
        public virtual async Task<IActionResult> AddOrderNote(AddOrderNoteModel model)
        {
            if (!_orderSettings.AllowCustomerToAddOrderNote)
                return RedirectToRoute("HomePage");

            if (!ModelState.IsValid)
            {
                return View("AddOrderNote", model);
            }

            var order = await _orderService.GetOrderById(model.OrderId);
            if (!await order.Access(_workContext.CurrentCustomer, _groupService))
                return Challenge();

            await _mediator.Send(new InsertOrderNoteCommand { Order = order, OrderNote = model, Language = _workContext.WorkingLanguage });

            //notification
            await _mediator.Publish(new OrderNoteEvent(order, model));

            Notification(NotifyType.Success, _translationService.GetResource("OrderNote.Added"), true);
            return RedirectToRoute("OrderDetails", new { orderId = model.OrderId });
        }

        //My account / Order details page / re-order
        [HttpGet]
        public virtual async Task<IActionResult> ReOrder(string orderId)
        {
            var order = await _orderService.GetOrderById(orderId);
            if (!await order.Access(_workContext.CurrentCustomer, _groupService))
                return Challenge();

            var warnings = await _mediator.Send(new ReOrderCommand { Order = order });
            if (warnings.Any())
                Notification(NotifyType.Error, string.Join(",", warnings), true);

            return RedirectToRoute("ShoppingCart");
        }

        //My account / Order details page / Complete payment
        [HttpPost]
        [AutoValidateAntiforgeryToken]
        public virtual async Task<IActionResult> RePostPayment(string orderId)
        {
            var order = await _orderService.GetOrderById(orderId);
            if (!await order.Access(_workContext.CurrentCustomer, _groupService))
                return Challenge();

            var paymentTransaction = await _paymentTransactionService.GetOrderByGuid(order.OrderGuid);

            if (paymentTransaction == null || !await _paymentService.CanRePostRedirectPayment(paymentTransaction))
                return RedirectToRoute("OrderDetails", new { orderId });

            await _paymentService.PostRedirectPayment(paymentTransaction);

            if (IsRequestBeingRedirected || IsPostBeingDone)
            {
                //redirection or POST has been done in PostProcessPayment
                return Content("Redirected");
            }

            //if no redirection has been done (to a third-party payment page)
            //theoretically it's not possible
            return RedirectToRoute("OrderDetails", new { orderId });
        }

        //My account / Order details page / Shipment details page
        [HttpGet]
        public virtual async Task<IActionResult> ShipmentDetails(string shipmentId, [FromServices] IShipmentService shipmentService)
        {
            var shipment = await shipmentService.GetShipmentById(shipmentId);
            if (shipment == null)
                return Challenge();

            var order = await _orderService.GetOrderById(shipment.OrderId);
            if (!await order.Access(_workContext.CurrentCustomer, _groupService))
                return Challenge();

            var model = await _mediator.Send(new GetShipmentDetails {
                Customer = _workContext.CurrentCustomer,
                Language = _workContext.WorkingLanguage,
                Order = order,
                Shipment = shipment
            });

            return View(model);
        }

        //My account / Loyalty points
        [HttpGet]
        [CustomerGroupAuthorize(SystemCustomerGroupNames.Registered)]
        public virtual async Task<IActionResult> CustomerLoyaltyPoints([FromServices] LoyaltyPointsSettings loyaltyPointsSettings)
        {
            if (!loyaltyPointsSettings.Enabled)
                return RedirectToRoute("CustomerInfo");

            var model = await _mediator.Send(new GetCustomerLoyaltyPoints {
                Customer = _workContext.CurrentCustomer,
                Store = _workContext.CurrentStore,
                Currency = _workContext.WorkingCurrency
            });
            return View(model);
        }
        #endregion
    }
}
