using Customer.Domain;
using Customer.Services;
using Microsoft.AspNetCore.Mvc;

namespace Customer.Api;

/// <summary>HTTP entry point for customer and order operations.</summary>
[ApiController]
[Route("api/[controller]")]
public class CustomerController : ControllerBase
{
    private readonly CustomerService _customerService;
    private readonly OrderService _orderService;

    public CustomerController(CustomerService customerService, OrderService orderService)
    {
        _customerService = customerService;
        _orderService = orderService;
    }

    [HttpGet("{id:int}")]
    public IActionResult GetCustomer(int id)
    {
        var customer = _customerService.GetCustomer(id);
        if (customer == null)
        {
            return NotFound();
        }

        return Ok(customer);
    }

    [HttpPost("orders")]
    public IActionResult PlaceOrder(int customerId, decimal grossTotal)
    {
        var total = _orderService.PlaceOrder(customerId, grossTotal);
        return Ok(total);
    }

    [HttpGet("{id:int}/discount")]
    public IActionResult GetDiscount(int id)
    {
        var customer = _customerService.GetCustomer(id);
        if (customer == null)
        {
            return NotFound();
        }

        var discount = _customerService.CalculateDiscount(customer);
        return Ok(discount);
    }
}
