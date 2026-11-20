"""Keep all profile editor, sidebar and usage messages complete and formattable."""
from collections import Counter
from string import Formatter
import unittest

from profile_text import PROFILE_TEXT


class ProfileTranslationTests(unittest.TestCase):
    def test_ten_locales_have_the_same_keys_and_placeholders(self):
        self.assertEqual(set(PROFILE_TEXT), {"pt", "en", "es", "fr", "de", "it", "ru", "ja", "zh", "ko"})
        en = PROFILE_TEXT["en"]
        formatter = Formatter()
        for language, catalog in PROFILE_TEXT.items():
            self.assertEqual(catalog.keys(), en.keys(), language)
            for key, value in catalog.items():
                with self.subTest(language=language, key=key):
                    self.assertIsInstance(value, str)
                    self.assertTrue(value.strip())
                    expected = Counter(token[1:] for token in formatter.parse(en[key]) if token[1] is not None)
                    actual = Counter(token[1:] for token in formatter.parse(value) if token[1] is not None)
                    self.assertEqual(actual, expected)
                    value.format(n=7, time="12:00")


if __name__ == "__main__":
    unittest.main()
