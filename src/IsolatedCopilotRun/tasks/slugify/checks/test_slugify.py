import sys
import unittest

sys.path.insert(0, ".")
from slugify import slugify  # noqa: E402


class SlugifyTests(unittest.TestCase):
    def test_lowercases_and_hyphenates(self):
        self.assertEqual(slugify("Hello World"), "hello-world")

    def test_collapses_separators(self):
        self.assertEqual(slugify("  a -- b__c  "), "a-b-c")

    def test_folds_accents(self):
        self.assertEqual(slugify("Crème Brûlée"), "creme-brulee")

    def test_keeps_digits(self):
        self.assertEqual(slugify("Release 2.0!"), "release-2-0")

    def test_empty_result(self):
        self.assertEqual(slugify("!!!"), "")


if __name__ == "__main__":
    unittest.main()
