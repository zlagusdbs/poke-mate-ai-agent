def is_applicable(order_amount, coupon):
    return order_amount > coupon["min_order_amount"]


def discount_amount(price, coupon):
    if not is_applicable(price, coupon):
        return 0
    amount = price * coupon["rate"] // 100
    return min(amount, coupon["max_discount"])
