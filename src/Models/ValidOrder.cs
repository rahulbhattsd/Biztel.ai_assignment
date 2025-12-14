public class ValidOrder
{
    public Guid OrderId { get; set; }
    public string CustomerName { get; set; }
    public DateTime OrderDate { get; set; }
    public decimal TotalAmount { get; set; }
    public bool IsHighValue { get; set; }
}
