export const cashStatuses=['pending','due_unpaid','part_paid','paid','no_amount_due'] as const;
export type CashStatus=typeof cashStatuses[number];
export const cashStatusLabels:Record<CashStatus,string>={pending:'Pending',due_unpaid:'Due · unpaid',part_paid:'Part paid',paid:'Paid',no_amount_due:'No amount due'};
export const isCashStatus=(value:unknown):value is CashStatus=>typeof value==='string'&&(cashStatuses as readonly string[]).includes(value);
