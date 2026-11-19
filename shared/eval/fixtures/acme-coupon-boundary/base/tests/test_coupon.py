import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from coupon import discount_amount


class DiscountTest(unittest.TestCase):
    def test_rate(self):
        self.assertEqual(discount_amount(10000, {"rate": 10}), 1000)


if __name__ == "__main__":
    unittest.main()
