-- Draft-only commodity exposure has a distinct capability from book-wide risk.
BEGIN;
INSERT INTO security.permissions VALUES('RunDealRisk');
INSERT INTO security.role_permissions SELECT id,'RunDealRisk' FROM security.roles WHERE code IN('trader','risk_analyst');
COMMIT;
