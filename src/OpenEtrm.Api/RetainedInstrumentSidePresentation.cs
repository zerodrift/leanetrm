using System.Collections.Immutable;
using System.Text.Json;
using System.Numerics;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

internal static class RetainedInstrumentSidePresentation
{
    internal sealed record Membership(int Slot,ExactDecimal Coefficient,ExactRatio? Ratio=null);
    internal static JsonElement GasComponent(ImmutableArray<JsonElement> components,int slot)
    {
        var selected=components.Where(member=>int.Parse(member.Text("slot"),System.Globalization.CultureInfo.InvariantCulture)==slot).ToArray();
        return selected.Length==1?selected[0]:throw Invalid();
    }
    internal static InstrumentSideGroup Compose(Guid instrument,ReferenceKey quantity,ReferenceKey currency,
        ImmutableArray<RetainedSideProgram> programs,ImmutableArray<Membership> memberships,ImmutableArray<InstrumentSideLabels> labels,string label)
    {
        if(instrument==Guid.Empty||programs.IsDefaultOrEmpty||!programs.Select(program=>program.Slot).SequenceEqual(Enumerable.Range(0,programs.Length))||
            programs.Any(program=>program.SideId==Guid.Empty||program.ScheduleId==Guid.Empty||program.Program is null||program.Identity!=program.Program.Identity)||
            memberships.IsDefault||memberships.Length!=programs.Length||!memberships.Select(member=>member.Slot).SequenceEqual(programs.Select(program=>program.Slot)))throw Invalid();
        var unit=new Quantity(ExactDecimal.Parse("1"),quantity);
        // Repeated Side IDs are legitimate reusable definitions. Each ordered
        // membership occurrence keeps its own slot and exact saved program.
        ImmutableArray<SideMembership> definitions=[..programs.Select((program,slot)=>new SideMembership(
            RetainedProgramSide.Define(program.SideId,program.Program,unit,currency),memberships[slot].Coefficient,memberships[slot].Ratio))];
        var annotated=labels.Select(member=>{
            if(member.Slot<0||member.Slot>=memberships.Length)throw Invalid();var saved=memberships[member.Slot];
            var values=ImmutableArray.CreateBuilder<InstrumentSideValue>();values.Add(new("Coefficient",saved.Coefficient.ToString()));
            if(saved.Ratio is {} ratio)values.Add(new("Quantity ratio",ratio.Numerator.ToString(System.Globalization.CultureInfo.InvariantCulture)+" / "+ratio.Denominator.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return member with{OverviewSections=member.OverviewSections.Add(new("Native membership",values.ToImmutable()))};
        }).ToImmutableArray();
        return InstrumentSidePresentation.Define(instrument,definitions,instrument,label,annotated);
    }

    internal static InstrumentSideGroup Gas(GasPosition position,ImmutableArray<RetainedSideProgram> programs,ImmutableArray<JsonElement> components)
    {
        var labels=ImmutableArray.CreateBuilder<InstrumentSideLabels>();var memberships=ImmutableArray.CreateBuilder<Membership>();
        if(components.IsEmpty)
        {
            var fee=position.Head.OptionalText("usage_fee") is not null||position.Head.OptionalText("usage_tariff_code") is not null;
            if(programs.Length!=(fee?3:2))throw Invalid();
            var direction=Sign(position.Head.Text("direction"));
            memberships.Add(new(0,ExactDecimal.Parse(direction.ToString(System.Globalization.CultureInfo.InvariantCulture)),new(1,1)));
            memberships.Add(new(1,ExactDecimal.Parse((-direction).ToString(System.Globalization.CultureInfo.InvariantCulture)),new(1,1)));
            labels.Add(Label(0,"Physical delivery","Contractual gas delivery",direction,true,position.Quantity,position.Currency));
            labels.Add(Label(1,"Consideration","Contractual gas consideration",-direction,false,position.Quantity,position.Currency));
            if(fee){var sign=Sign(position.Head.Text("usage_fee_direction"));memberships.Add(new(2,ExactDecimal.Parse(sign.ToString(System.Globalization.CultureInfo.InvariantCulture)),new(1,1)));
                labels.Add(Label(2,"Usage fee","Contractual usage fee",sign,false,position.Quantity,position.Currency));}
        }
        else
        {
            if(components.Length!=programs.Length)throw Invalid();
            foreach(var program in programs)
            {
                var member=GasComponent(components,program.Slot);var role=member.Text("role");
                if(role is not("physical" or "consideration" or "indexed_consideration" or "fee" or "charge"))throw Invalid();
                var physical=role=="physical";
                memberships.Add(new(program.Slot,ExactDecimal.Parse(member.Text("direction")),new(BigInteger.Parse(member.Text("ratio_n"),System.Globalization.CultureInfo.InvariantCulture),BigInteger.Parse(member.Text("ratio_d"),System.Globalization.CultureInfo.InvariantCulture))));
                if(program.Program.Document.Family!=(physical?"gas":role=="indexed_consideration"?"floating":"fixed"))throw Invalid();
                labels.Add(Label(program.Slot,member.Text("code"),role switch{"physical"=>"Contractual gas delivery","fee"=>"Contractual usage fee","charge"=>"Contractual gas charge",_=>"Contractual gas consideration"},
                    Sign(member.Text("direction")),physical,position.Quantity,position.Currency));
            }
        }
        return Compose(position.Binding,position.Quantity,position.Currency,programs,memberships.ToImmutable(),labels.ToImmutable(),"Retained gas contract composition");
    }

    internal static InstrumentSideGroup Crude(CrudePosition position,ImmutableArray<RetainedSideProgram> programs)
    {
        if(programs.Length!=(position.Head.OptionalText("handling_fee") is null?2:3))throw Invalid();
        var labels=programs.Select(program=>Label(program.Slot,program.Slot switch{0=>"Crude delivery",1=>"Fixed consideration",_=>"Handling fee"},
            program.Slot==0?"Contractual crude delivery":"Contractual cash consideration",
            Sign(position.Head.Text(program.Slot switch{0=>"direction",1=>"consideration_direction",_=>"handling_fee_direction"})),program.Slot==0,position.Quantity,position.Currency)).ToImmutableArray();
        return Compose(position.Binding,position.Quantity,position.Currency,programs,[..programs.Select(program=>new Membership(program.Slot,ExactDecimal.Parse(position.Head.Text(program.Slot switch{0=>"direction",1=>"consideration_direction",_=>"handling_fee_direction"}))))],labels,"Retained crude contract composition");
    }

    internal static InstrumentSideGroup Lng(LngPosition position,ImmutableArray<RetainedSideProgram> programs)
    {
        if(programs.Length!=2)throw Invalid();
        var labels=programs.Select(program=>Label(program.Slot,program.Slot==0?"LNG cargo":"Fixed consideration",program.Slot==0?"Contractual cargo delivery":"Contractual cash consideration",
            Sign(position.Head.Text(program.Slot==0?"direction":"cash_direction")),program.Slot==0,position.Quantity,position.Currency)).ToImmutableArray();
        return Compose(position.Binding,position.Quantity,position.Currency,programs,[..programs.Select(program=>new Membership(program.Slot,ExactDecimal.Parse(position.Head.Text(program.Slot==0?"direction":"cash_direction"))))],labels,"Retained LNG contract composition");
    }

    internal static InstrumentSideGroup Operating(OperatingPosition position,ImmutableArray<RetainedSideProgram> programs)
    {
        var members=position.Head.GetProperty("members").EnumerateArray().ToArray();
        if(members.Length!=programs.Length)throw Invalid();var labels=ImmutableArray.CreateBuilder<InstrumentSideLabels>();
        var composition=OperatingComposition(position);
        if(composition is not null&&composition.Sides.Length!=programs.Length)throw Invalid();
        foreach(var program in programs)
        {
            var selected=members.Where(member=>member.GetProperty("slot").GetInt32()==program.Slot).ToArray();
            if(selected.Length!=1||selected[0].Id("sideId")!=program.SideId)throw Invalid();var member=selected[0];var name=member.Text("name");
            if(composition is not null)
            {
                if(program.Slot<0||program.Slot>=composition.Sides.Length)throw Invalid();
                var declaration=composition.Sides[program.Slot];var direction=Sign(declaration.Coefficient);var delivery=declaration.Kind=="delivery";
                if(name!=declaration.Name||Sign(member.Text("coefficient"))!=direction)throw Invalid();
                var quoteUnit=OperatingSideProgramProof.Require(position,declaration,program.Program);
                var guards=program.Program.Document.Instructions.Where(row=>row.Output.Operation=="output_guard").ToArray();
                if(guards.Length==0||guards.Any(row=>row.Output.Parameters.GetValueOrDefault("lifecycle")!=(delivery?OperatingContractScheduleSource.DeliveryLifecycle:"undue_payment_v1")||
                    delivery&&row.Output.Parameters.GetValueOrDefault("paymentRule")!="not_applicable_delivery_v1"||
                    !row.Inputs.TryGetValue("amount",out var amount)||amount.Length!=1||
                    !program.Program.Document.Roots.Any(root=>root.Row==amount[0]&&root.Measure==(delivery?"physical_value":"contractual_amount"))))throw Invalid();
                var label=Label(program.Slot,declaration.Name,delivery?"Operating-right delivery":"Operating-right payment",direction,delivery,position.Quantity,position.Currency);
                labels.Add(label with{OverviewSections=label.OverviewSections.Add(OperatingTerms(declaration,quoteUnit))});
                continue;
            }
            var sign=name switch{"discharging_energy" or "energy_output" or "payment_energy"=>1,"charging_energy" or "fuel_input" or "variable_fee" or "access_fee"=>-1,_=>throw Invalid()};
            if(Sign(member.Text("coefficient"))!=sign)throw Invalid();
            labels.Add(Label(program.Slot,name.Replace('_',' '),"Operating-right "+name.Replace('_',' '),sign,false,position.Quantity,position.Currency));
        }
        return Compose(position.Binding,position.Quantity,position.Currency,programs,[..programs.Select(program=>new Membership(program.Slot,ExactDecimal.Parse(members.Single(member=>member.GetProperty("slot").GetInt32()==program.Slot).Text("coefficient"))))],labels.ToImmutable(),"Retained operating-right composition");
    }

    static StructuredPowerCompositionInput? OperatingComposition(OperatingPosition position)
    {
        try
        {
            var plan=position.Head.GetProperty("request").GetProperty("plan");
            var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var valuation=plan.GetProperty("valuation");
            if(!plan.TryGetProperty("composition",out var saved))
            {
                var legacy=valuation.Deserialize<StructuredPowerValuationInput>(options)??throw Invalid();
                StructuredPowerPreview.ValidateValuationProfile(legacy.Perspective,legacy.Measure,legacy.Discounting);
                if(legacy.EnergyPrice is null||legacy.PaymentRule is null||legacy.CurrencyCode!=position.Currency.Code||legacy.RightQuantityCode!=position.Quantity.Code)throw Invalid();
                _=StructuredPowerPreview.OperatingPayment(legacy.PaymentRule);
                return null;
            }
            if(valuation.ValueKind!=JsonValueKind.Null)throw Invalid();
            var composition=saved.Deserialize<StructuredPowerCompositionInput>(options)??throw Invalid();
            StructuredPowerPreview.ValidateComposition(composition,position.Physical.Text("kind"));
            if(composition.CurrencyCode!=position.Currency.Code||composition.RightQuantityCode!=position.Quantity.Code)throw Invalid();
            return composition;
        }
        catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException or MarketDataException){throw Invalid();}
    }

    static InstrumentSideSection OperatingTerms(StructuredPowerOperatingSideInput side,PriceUnit quoteUnit)
    {
        var price=side.Price;var values=ImmutableArray.CreateBuilder<InstrumentSideValue>();
        values.Add(new("Side key",side.Key));values.Add(new("Quantity source",side.QuantitySource));values.Add(new("Side kind",side.Kind));
        values.Add(new("Price policy",price.Mode));values.Add(new("Quote",price.QuoteCode));
        values.Add(new("Quote quantity",quoteUnit.Quantity.Code));values.Add(new("Quote multiplier",quoteUnit.Multiplier.ToString()));
        if(price.Mode=="fixed"&&price.FixedPrice is {} amount&&price.CurveCode is null&&price.ContractId is null)values.Add(new("Fixed price",amount));
        else if(price.Mode=="selected_contract_flat_v1"&&price.FixedPrice is null&&price.CurveCode is {} curve&&price.ContractId is {} contract)
        {values.Add(new("Market curve",curve));values.Add(new("Market contract",contract.ToString("D")));}
        else throw Invalid();
        if(side.Kind=="payment")
        {
            var payment=side.PaymentRule!;_ = StructuredPowerPreview.OperatingPayment(payment);
            values.Add(new("Payment policy",payment.Implementation));values.Add(new("Payment rule",payment.Rule));
            values.Add(new("Payment days after delivery month",payment.Days!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            values.Add(new("Payment day basis",payment.DayBasis!));values.Add(new("Date adjustment",payment.Adjustment));
        }
        else values.Add(new("Payment rule","Not applicable to delivery"));
        return new("Retained operating Side terms",values.ToImmutable());
    }

    internal static InstrumentSideGroup Rec(RecPosition position,ImmutableArray<RetainedSideProgram> programs)
    {
        var members=position.Head.GetProperty("members").EnumerateArray().ToArray();
        if(programs.Length!=2||members.Length!=2)throw Invalid();var labels=ImmutableArray.CreateBuilder<InstrumentSideLabels>();
        foreach(var program in programs)
        {
            var selected=members.Where(member=>member.GetProperty("slot").GetInt32()==program.Slot).ToArray();
            if(selected.Length!=1||selected[0].Id("sideId")!=program.SideId)throw Invalid();
            labels.Add(Label(program.Slot,program.Slot==0?"Certificate delivery":"Consideration",program.Slot==0?"Contractual certificate delivery":"Contractual cash consideration",
                Sign(selected[0].Text("coefficient")),program.Slot==0,position.Quantity,position.Currency));
        }
        return Compose(position.Binding,position.Quantity,position.Currency,programs,[..programs.Select(program=>new Membership(program.Slot,ExactDecimal.Parse(members.Single(member=>member.GetProperty("slot").GetInt32()==program.Slot).Text("coefficient"))))],labels.ToImmutable(),"Retained REC forward composition");
    }

    static InstrumentSideLabels Label(int slot,string label,string role,int direction,bool physical,ReferenceKey quantity,ReferenceKey currency)=>new(slot,label,role,
        direction==1?physical?"Company receives delivery":"Company receives proceeds":physical?"Company delivers":"Company pays",[
            new("Contract terms",[new("Quantity specification",quantity.Code),new("Currency",currency.Code),
                new("State","Retained contractual definition; current value and performance are reported independently.")])]);
    static int Sign(string value)
    {var sign=ExactDecimal.Parse(value);return sign==ExactDecimal.Parse("1")?1:sign==ExactDecimal.Parse("-1")?-1:throw Invalid();}
    static MarketDataException Invalid()=>new("instrument_side_presentation","Retain every exact ordered native Side program and its declared role, direction and binding.");
}
