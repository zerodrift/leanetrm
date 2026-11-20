# Gas facility trading desk

A commercial facility profile identifies a physical site, its pricing points and the published definitions used for booking. Open **Facilities → Trades and volumes** to select a profile, inspect its pricing and ratchets, and open a linked trade in the Blotter. Choose a business date for capacity in force and a delivery window for volume. The end of the volume window is exclusive.

## Set up a bilateral desk

In **Users & access**, assign the **Contracts administration**, **Counterparty credit** and **Trade confirmations** roles to the people responsible for each function.

1. Create the trading book, legal entity and counterparty. Publish the gas delivery topology, commodity, quantity, operating clock and valuation assignments. A thermal plant also needs a power settlement curve at its selected location and an explicitly compatible energy quote.
2. On **Legal entities & counterparties**, the contracts officer records the agreement, its exact parties, covered products, effective date, expiry, procedure and special provisions. Attach the executed PDF. Each revision retains its document digest. A user with document access can download the filed revision.
3. The credit officer records a separate, product-specific clearance for the legal entity and counterparty. The booking operation checks the executed agreement at server-recorded execution time and the clearance in force when admitting the trade. Where several agreements apply, the trader chooses one. An economic amendment needs its own admission. Existing trade revisions retain their original evidence.
4. Define the physical facility and its operating limits. In **Trades and volumes**, choose **Configure commercial profile**. For storage, choose the version and injection and withdrawal ports, then enter commercial ratchet bands. The two pricing ports must share the contract’s operating clock and gas-day start. For a thermal plant, choose its version, gas receipt point and power settlement index.
5. Book the trade against its facility. A commercial confirmations user can record a matched or disputed confirmation against the exact booked revision. The shipper and operator can separately record service accounts, nominations, responses and adopted schedules on physical gas obligations.

Commercial storage grants the holder the right to inject and withdraw. Its five economic Sides value withdrawals, injections, injection fees, withdrawal fees and capacity fees. A gas purchase delivered into storage has its own physical receipt Side and may have a floating payment Side. A thermal toll pins the operating plant and plan. A cash-settled heat-rate call can carry an optional plant link for reporting.

Storage operating ratchets use total physical inventory and units per elapsed hour. The commercial bands use working inventory and units per gas day. On the storage ticket the profile supplies the capacity range, suggested contract ratchets, clock, curves and location price adjustments. The booked contract retains its negotiated terms.

## Read facility volumes

| Measure | Source |
| --- | --- |
| Booked gas receipts and deliveries | Physical Sides that match the facility ports, clipped to the selected gas days |
| Storage capacity in force | Contracted working capacity on the selected business date |
| Planned injections, withdrawals, fuel and power | Recorded storage plans and the booked operating right |
| Nominated, confirmed and scheduled gas | The respective quantity series on the linked physical obligations |
| Measured and allocated flow | Reported facility quantities and their explicit allocation plans |
| Financial capacity and swap notionals | Attributed financial trades in their native units |

Each meter row gives its gas day, point and native unit. A corrected statement can leave an earlier allocation in place. For example, revising a measurement from 94 to 90 with 92 allocated displays −2 unallocated and identifies the earlier meter revision. A missing report appears as unreported. The selected historical knowledge cutoff governs which revisions the desk can show.
