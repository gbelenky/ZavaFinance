"""Exact, half-open monthly periods. Unrecognized text never defaults to a broader range."""

import re
from dataclasses import dataclass
from datetime import date

MONTH_NAMES = ("January", "February", "March", "April", "May", "June", "July",
               "August", "September", "October", "November", "December")


def add_months(value: date, months: int) -> date:
    year, month = divmod(value.year * 12 + value.month - 1 + months, 12)
    return date(year, month + 1, 1)


@dataclass(frozen=True)
class FinancePeriod:
    start: date
    end: date
    label: str

    @property
    def first_month(self) -> date:
        return self.start

    @property
    def closing_month(self) -> date:
        return add_months(self.end, -1)

    @property
    def month_count(self) -> int:
        return (self.end.year - self.start.year) * 12 + self.end.month - self.start.month

    def prior_year(self) -> "FinancePeriod":
        return FinancePeriod(add_months(self.start, -12), add_months(self.end, -12), f"{self.label} (prior year)")

    def prior_period(self) -> "FinancePeriod":
        return FinancePeriod(add_months(self.start, -self.month_count), self.start, f"{self.label} (prior period)")


def _month(value: date) -> FinancePeriod:
    return FinancePeriod(value, add_months(value, 1), f"{MONTH_NAMES[value.month - 1]} {value.year:04d}")


def _quarter(year: int, quarter: int) -> FinancePeriod:
    start = date(year, (quarter - 1) * 3 + 1, 1)
    return FinancePeriod(start, add_months(start, 3), f"Q{quarter} {year}")


def _year(year: int) -> FinancePeriod:
    return FinancePeriod(date(year, 1, 1), date(year + 1, 1, 1), str(year))


def _month_index(name: str) -> int:
    return next((i for i, month in enumerate(MONTH_NAMES, 1)
                 if len(name) >= 3 and month.lower().startswith(name)), 0)


class FinancePeriodParser:
    @staticmethod
    def parse(text: str | None, today: date) -> FinancePeriod | None:
        if not text or not text.strip():
            return None
        value = text.strip().lower()
        try:
            this_month = date(today.year, today.month, 1)
            if value in ("this month", "current month"):
                return _month(this_month)
            if value in ("last month", "previous month"):
                return _month(add_months(this_month, -1))
            if value in ("this quarter", "current quarter"):
                return _quarter(today.year, (today.month - 1) // 3 + 1)
            if value in ("last quarter", "previous quarter"):
                previous = add_months(this_month, -3)
                return _quarter(previous.year, (previous.month - 1) // 3 + 1)
            if value in ("this year", "current year", "ytd", "year to date"):
                return FinancePeriod(date(today.year, 1, 1), add_months(this_month, 1), f"YTD {today.year}")
            if value in ("last year", "previous year"):
                return _year(today.year - 1)
            match = re.fullmatch(r"q([1-4])\s*(?:of\s*)?(?:fy)?\s*(\d{4})", value)
            if match:
                return _quarter(int(match[2]), int(match[1]))
            match = re.fullmatch(r"(\d{4})\s*q([1-4])", value)
            if match:
                return _quarter(int(match[1]), int(match[2]))
            match = re.fullmatch(r"([a-z]+)\s*(\d{4})?\s*(?:to|through|thru|until|-|–|—)\s*([a-z]+)\s*(\d{4})", value)
            if match:
                first, last = _month_index(match[1]), _month_index(match[3])
                if not first or not last:
                    return None
                year = int(match[4])
                start = date(int(match[2]) if match[2] else year, first, 1)
                end = add_months(date(year, last, 1), 1)
                return None if end <= start else FinancePeriod(start, end, f"{match[1].capitalize()} to {match[3].capitalize()} {year}")
            match = re.fullmatch(r"([a-z]+)\s+(\d{4})", value)
            if match and _month_index(match[1]):
                return _month(date(int(match[2]), _month_index(match[1]), 1))
            match = re.fullmatch(r"h([12])\s*(?:fy)?\s*(\d{4})", value)
            if match:
                half, year = int(match[1]), int(match[2])
                start = date(year, 1 if half == 1 else 7, 1)
                return FinancePeriod(start, add_months(start, 6), f"H{half} {year}")
            match = re.fullmatch(r"(?:fy)?\s*(\d{4})", value)
            return _year(int(match[1])) if match else None
        except (ValueError, OverflowError):
            return None
