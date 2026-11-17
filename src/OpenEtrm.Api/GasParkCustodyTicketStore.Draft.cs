using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

internal sealed partial class GasParkCustodyTicketStore
{
    internal static JsonElement Draft(GasParkCustodyBooking b,InputFrame frame,MarketSnapshot snapshot)=>JsonSerializer.SerializeToElement(new{
        implementation=b.Profile,tradeId=GasParkAndLoanValuation.PreviewIdentity(b.OperationId,frame.Id,"trade"),
        instrumentId=GasParkAndLoanValuation.PreviewIdentity(b.OperationId,frame.Id,"instrument"),revision=1,
        b.BookId,b.Quantity,b.OwnedAccountId,b.LegalEntityId,b.LegalEntityRevision,b.CounterpartyId,b.CounterpartyRevision,
        publicationOrder=0,tradeReference=(int?)null,instrumentReference=(int?)null,reconciliationRequired=false,
        effectiveAt=frame.At,contextId=frame.ContextId,asOf=frame.At,knownAt=snapshot.Knowledge!.KnownAt,effectiveOn=frame.BusinessDate,
        terms=new{b.DefinitionRevisionId,b.ReferenceCatalogId,b.ReferenceRevisionId,b.Point,quantity=b.QuantitySpecification,b.Currency,
            b.ServiceKind,b.Perspective,b.ParkAt,b.ReturnAt,b.TitlePolicy,b.ReturnPolicy,b.ReturnTiming,b.ReturnOrder,b.UsePolicy,b.LossPolicy,
            b.PhysicalPolicy,b.PlanPolicy,b.FixedFeeAmount,b.FeeDirection,b.FeeDueAt,b.FeeRecognition},
        evidence=Array.Empty<object>(),evidenceHeads=Array.Empty<object>(),acceptance=(object?)null,returns=Array.Empty<object>(),
        reservations=Array.Empty<object>(),balances=new{inCustody="0",reserved="0",overdue="0",ownedQuantityChange="0"},cash=(object?)null
    },GasOwnedOperations.Json);
}
