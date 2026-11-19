import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from coupon import discount_amount

COUPON = {"rate": 10, "min_order_amount": 10000, "max_discount": 3000}


class DiscountTest(unittest.TestCase):
    def test_rate(self):
        self.assertEqual(discount_amount(20000, COUPON), 2000)

    def test_below_min_order(self):
        self.assertEqual(discount_amount(9000, COUPON), 0)

    def test_max_discount(self):
        self.assertEqual(discount_amount(50000, COUPON), 3000)


if __name__ == "__main__":
    unittest.main()
