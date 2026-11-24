"""A migration that splices text into a PL/pgSQL IF condition must leave the condition whole.

PL/pgSQL ends the condition of `IF ... THEN` at the first THEN outside parentheses, so a CASE expression
spliced into one has to be parenthesized: its own WHEN ... THEN would otherwise end the condition early and
the patched function would not compile."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]


def sql_text(expression):
    """The value of a concatenation of SQL string literals."""
    literal = r"E?'(?:[^']|'')*'"
    if not re.fullmatch(rf"\s*{literal}(\s*\|\|\s*{literal})*\s*", expression):
        raise AssertionError('Not a concatenation of string literals: ' + expression)
    return ''.join(part[part.index("'") + 1:-1].replace("''", "'") for part in re.findall(literal, expression))


def condition_of(statement):
    """The condition PL/pgSQL reads from `IF <condition> THEN ...`: up to the first THEN outside parentheses and literals."""
    assert statement.startswith('IF ')
    depth, position = 0, 3
    while position < len(statement):
        character = statement[position]
        if character == "'":
            position += 1
            while statement[position] != "'" or statement[position + 1:position + 2] == "'":
                position += 2 if statement[position] == "'" else 1
        elif character in '()':
            depth += 1 if character == '(' else -1
        elif depth == 0 and re.match(r'THEN\b', statement[position:], re.I) and not statement[position - 1].isalnum():
            return statement[3:position].strip()
        position += 1
    raise AssertionError('No THEN ends the condition: ' + statement)


class SplicedConditionChecks(unittest.TestCase):
    def test_a_parenthesized_case_stays_inside_its_condition_and_a_bare_one_does_not(self):
        bare = "x IS DISTINCT FROM CASE k WHEN 'a' THEN 'b' END"
        self.assertEqual(condition_of(f"IF {bare} THEN RAISE EXCEPTION 'e';END IF;"), "x IS DISTINCT FROM CASE k WHEN 'a'")
        whole = "x IS DISTINCT FROM (CASE k WHEN 'a' THEN 'b' END)"
        self.assertEqual(condition_of(f"IF {whole} THEN RAISE EXCEPTION 'e';END IF;"), whole)
        self.assertEqual(condition_of("IF note='THEN' AND(a OR b) THEN x=1;END IF;"), "note='THEN' AND(a OR b)")

    def test_the_surface_solver_policy_of_a_product_is_one_whole_condition(self):
        migration = (ROOT / 'db/migrations/0398_european_option_surfaces.sql').read_text()
        style_policy = sql_text(re.search(r'style_policy text=(.*?);\nBEGIN', migration, re.S).group(1))
        self.assertIn("WHEN 'american' THEN 'american_futures_option_crr_v1'", style_policy)
        self.assertIn("WHEN 'european' THEN 'european_futures_option_black76_v1'", style_policy)
        self.assertNotIn('ELSE', style_policy)
        # The two places the migration splices it: the recording's input test and the fit admission's product pin.
        for condition in ('catalog IS NULL OR policy IS DISTINCT FROM ' + style_policy + ' OR inputs IS NULL',
                          "request->>'solverPolicy' IS DISTINCT FROM " + style_policy):
            self.assertEqual(condition_of(f"IF {condition} THEN RAISE EXCEPTION 'synthetic';END IF;"), condition)


if __name__ == '__main__':
    unittest.main()
