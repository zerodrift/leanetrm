"""Explicit financial bindings for synthetic publication authoring checks."""


def index_binding(choice, *, symbol, statistic):
    return {key:choice[key] for key in ('key','commodity','location','quote','unit')} | dict(symbol=symbol,statistic=statistic)
