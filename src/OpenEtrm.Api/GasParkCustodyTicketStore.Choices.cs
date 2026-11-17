using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;

namespace OpenEtrm.Api;

internal sealed partial class GasParkCustodyTicketStore
{
    internal Task<object> Choices(NpgsqlConnection connection,PreviewData data,InputFrame frame,TicketFieldRequest request,CancellationToken ct)=>
        Certified<object>(connection,data,frame,async(d,f)=>
        {
            if(request.Draft.Product!=GasParkLoanTicket.Product||request.Side is not null||!GasParkCustodyTicket.Describe(request.Draft).TradeFields.Any(field=>field.Id==request.Field))
                throw GasParkCustodyTicket.Invalid(request.Field,"Select a declared custody agreement field.");
            if(GasParkCustodyTicket.StaticChoices(request.Field) is {} fixedChoices)return new TicketChoices(fixedChoices,null);
            if(request.Field=="book")return new TicketChoices([..(await BookScope.Read(connection,database.Manifest.CatalogId,ct)).Select(b=>new TicketChoice(b.Id("id").ToString(),b.Text("name")))],null);
            if(request.Field is "legal_entity" or "counterparty")
                return new TicketChoices([..(await TradeParties.Catalog(connection,database.Manifest.CatalogId,request.Field=="legal_entity",ct)).Select(p=>new TicketChoice($"{p.Id}@{p.Revision}",p.Name))],null);
            var reference=d.ReferenceRevision;
            if(request.Field=="reference_bundle")return new TicketChoices([new($"{reference.CatalogId}@{reference.RevisionId}",$"Exact reference bundle {reference.RevisionId}")],null);
            var values=request.Draft.Trade;
            string Required(string field)=>values.TryGetValue(field,out var value)&&!string.IsNullOrWhiteSpace(value)?value:throw GasParkCustodyTicket.Invalid(field,"Select this custody source dependency explicitly.");
            if(Required("reference_bundle")!=$"{reference.CatalogId}@{reference.RevisionId}")throw GasParkCustodyTicket.Invalid("reference_bundle","Select the exact reference bundle captured by this source.");
            if(request.Field is "point" or "quantity_specification" or "currency")
            {
                var sql=request.Field switch
                {
                    "point"=>"SELECT jsonb_build_object('code',p.point_code,'label',p.pipeline_code||' · '||p.zone_code||' · '||p.point_code)::text FROM ref_data.pipeline_delivery_points p WHERE(p.catalog_id,p.revision_id)=($1,$2) AND EXISTS(SELECT 1 FROM ref_data.gas_delivery_conventions d WHERE(d.catalog_id,d.revision_id,d.pipeline_code,d.zone_code,d.point_code)=(p.catalog_id,p.revision_id,p.pipeline_code,p.zone_code,p.point_code)) ORDER BY p.point_code",
                    "quantity_specification"=>"SELECT jsonb_build_object('code',q.code,'label',q.code||' · '||u.symbol)::text FROM ref_data.quantity_specifications q JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code) WHERE(q.catalog_id,q.revision_id)=($1,$2) AND EXISTS(SELECT 1 FROM ref_data.gas_delivery_conventions d WHERE(d.catalog_id,d.revision_id,d.point_code,d.product_code)=(q.catalog_id,q.revision_id,$3,q.commodity_code)) ORDER BY q.code",
                    _=>"SELECT jsonb_build_object('code',c.code,'label',c.display_code)::text FROM ref_data.currencies c WHERE(c.catalog_id,c.revision_id)=($1,$2) ORDER BY c.code"
                };
                var parameters=request.Field=="quantity_specification"?new object[]{reference.CatalogId,reference.RevisionId,Required("point")}:new object[]{reference.CatalogId,reference.RevisionId};
                var rows=await PreviewDatabase.Rows(connection,sql,parameters,ct);
                return new TicketChoices([..rows.Select(r=>new TicketChoice(r.Text("code"),r.Text("label")))],null);
            }
            if(request.Field!="owned_account")return new TicketChoices([],null);
            var perspective=Required("perspective");
            if(perspective=="provider")return new TicketChoices([new(GasParkCustodyTicket.NoAccount,"Not applicable: customer-owned custody")],null);
            if(perspective!="customer")throw GasParkCustodyTicket.Invalid("perspective","Choose the customer or provider custody perspective explicitly.");
            if(!Guid.TryParse(Required("book"),out var book)||book==Guid.Empty)throw GasParkCustodyTicket.Invalid("book","Select an exact custody book.");
            var entity=Required("legal_entity").Split('@');
            if(entity.Length!=2||!Guid.TryParse(entity[0],out var entityId)||!int.TryParse(entity[1],out var entityRevision))throw GasParkCustodyTicket.Invalid("legal_entity","Select the exact legal entity revision.");
            var point=Required("point");var quantity=Required("quantity_specification");
            var state=await GasOwnedOperations.Read(connection,database,Context(f),null,ct);
            var accounts=state.GetProperty("accounts").EnumerateArray().Where(a=>a.Text("kind")=="owned_inventory"&&a.Id("book_id")==book&&
                a.Id("legal_entity_id")==entityId&&a.GetProperty("legal_entity_revision").GetInt32()==entityRevision&&
                a.Id("reference_catalog_id")==reference.CatalogId&&a.Id("reference_revision_id")==reference.RevisionId&&a.Text("location_code")==point&&a.Text("quantity_code")==quantity);
            return new TicketChoices([..accounts.Select(a=>new TicketChoice(a.Id("id").ToString(),$"{a.Text("code")} · {point} · {quantity}"))],null);
        },ct);

    async Task RequireBookingSource(NpgsqlConnection connection,PreviewData data,InputFrame frame,GasParkCustodyBooking body,CancellationToken ct)
    {
        var reference=data.ReferenceRevision;
        if(body.ReferenceCatalogId!=reference.CatalogId||body.ReferenceRevisionId!=reference.RevisionId||body.DefinitionRevisionId!=data.DefinitionRevision.RevisionId)
            throw GasParkCustodyTicket.Invalid("reference_bundle","Select the exact captured definition and reference source; this profile has no cross-bundle mapping.");
        var rows=await PreviewDatabase.Rows(connection,"""
            SELECT jsonb_build_object('point',p.point_code)::text FROM ref_data.pipeline_delivery_points p
            JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=($1,$2,$4)
            JOIN ref_data.currencies c ON(c.catalog_id,c.revision_id,c.code)=($1,$2,$5)
            WHERE(p.catalog_id,p.revision_id,p.point_code)=($1,$2,$3) AND EXISTS(
             SELECT 1 FROM ref_data.gas_delivery_conventions d WHERE(d.catalog_id,d.revision_id,d.pipeline_code,d.zone_code,d.point_code,d.product_code)=
              (p.catalog_id,p.revision_id,p.pipeline_code,p.zone_code,p.point_code,q.commodity_code))
            """,[reference.CatalogId,reference.RevisionId,body.Point,body.QuantitySpecification,body.Currency],ct);
        if(rows.Length!=1)throw GasParkCustodyTicket.Invalid("point","Choose one exact compatible custody point, quantity specification and independent currency in the captured bundle.");
        if(body.Perspective=="provider")
        {
            if(body.OwnedAccountId is not null)throw GasParkCustodyTicket.Invalid("owned_account","Provider custody has no owned inventory account.");
            return;
        }
        var state=await GasOwnedOperations.Read(connection,database,Context(frame),body.OwnedAccountId,ct);
        var accounts=state.GetProperty("accounts").EnumerateArray().Where(a=>a.OptionalId("id")==body.OwnedAccountId&&a.Text("kind")=="owned_inventory"&&
            a.Id("book_id")==body.BookId&&a.Id("legal_entity_id")==body.LegalEntityId&&a.GetProperty("legal_entity_revision").GetInt32()==body.LegalEntityRevision&&
            a.Id("reference_catalog_id")==reference.CatalogId&&a.Id("reference_revision_id")==reference.RevisionId&&
            a.Text("location_code")==body.Point&&a.Text("quantity_code")==body.QuantitySpecification).ToArray();
        if(accounts.Length!=1)throw GasParkCustodyTicket.Invalid("owned_account","Choose this customer's exact inventory account, legal entity, book, point and quantity basis.");
    }
}
