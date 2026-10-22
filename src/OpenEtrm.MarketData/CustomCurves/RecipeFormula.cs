using System.Collections.Immutable;
using System.Text.Json;

namespace OpenEtrm.MarketData.CustomCurves;

// A textual representation of the typed graph, not a scripting language.
// String parameters are JSON strings; @name is a typed edge to another node.
public static class RecipeFormula
{
    public static RecipeDefinition Compile(string formula,string code,string name)
    {
        if(string.IsNullOrWhiteSpace(formula)||formula.Length>16384)throw Invalid("Supply a formula up to 16,384 characters.");
        var parser=new Parser(formula);var nodes=ImmutableArray.CreateBuilder<RecipeNode>();
        while(!parser.End)
        {
            if(nodes.Count>=64)throw Invalid("A formula can contain at most 64 nodes.");
            var id=parser.Identifier();parser.Take('=');var kind=parser.Identifier();parser.Take('(');
            var parameters=ImmutableDictionary.CreateBuilder<string,string>();var inputs=ImmutableDictionary.CreateBuilder<string,string>();
            var weights=default(ImmutableArray<RecipeContractWeight>);
            PublishedRecipeReference? published=null;
            var panelWeights=default(ImmutableArray<RecipePanelWeight>);
            if(!parser.Peek(')'))
            {
                do
                {
                    var key=parser.Identifier();parser.Take('=');
                    if(parameters.ContainsKey(key)||inputs.ContainsKey(key)||key=="contractWeights"&&!weights.IsDefault||key=="panelWeights"&&!panelWeights.IsDefault||key=="publishedSource"&&published is not null)throw Invalid("Each argument may appear once.");
                    if(key=="contractWeights")weights=parser.Weights();
                    else if(key=="panelWeights")panelWeights=parser.PanelWeights();
                    else if(key=="publishedSource")published=parser.PublishedSource();
                    else if(parser.Peek('@')){parser.Take('@');inputs.Add(key,parser.Identifier());}
                    else parameters.Add(key,parser.String());
                    if(!parser.Peek(','))break;parser.Take(',');
                }while(true);
            }
            parser.Take(')');
            nodes.Add(new(id,kind,parameters.ToImmutable(),inputs.ToImmutable(),weights,published,panelWeights));
        }
        var outputs=nodes.Where(n=>n.Kind=="output").ToArray();
        if(outputs.Length!=1)throw Invalid("A formula requires exactly one output node.");
        var definition=new RecipeDefinition(code,name,nodes.ToImmutable(),outputs[0].Id);
        RecipeEngine.Validate(definition);return definition;
    }
    public static string Format(RecipeDefinition definition)
    {
        RecipeEngine.Validate(definition);
        foreach(var node in definition.Nodes)
            if(!IdentifierValid(node.Id)||!IdentifierValid(node.Kind)||node.Parameters.Keys.Concat(node.Inputs.Keys).Any(key=>!IdentifierValid(key)))
                throw Invalid("Formula identities must be identifiers beginning with a letter.");
        return string.Join("\n",definition.Nodes.Select(node=>node.Id+" = "+node.Kind+"("+
            string.Join(", ",node.Parameters.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Key+"="+JsonSerializer.Serialize(p.Value))
                .Concat(node.Inputs.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Key+"=@"+p.Value))
                .Concat(node.ContractWeights.IsDefaultOrEmpty?[]:new[]{"contractWeights="+JsonSerializer.Serialize(node.ContractWeights,new JsonSerializerOptions(JsonSerializerDefaults.Web))})
                .Concat(node.PanelWeights.IsDefaultOrEmpty?[]:new[]{"panelWeights="+JsonSerializer.Serialize(node.PanelWeights,new JsonSerializerOptions(JsonSerializerDefaults.Web))})
                .Concat(node.PublishedSource is null?[]:new[]{"publishedSource="+JsonSerializer.Serialize(node.PublishedSource,new JsonSerializerOptions(JsonSerializerDefaults.Web))}))+")"));
    }
    static bool IdentifierValid(string text)=>text.Length is >0 and <=80&&char.IsAsciiLetter(text[0])&&text.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_');
    static MarketDataException Invalid(string message)=>new("recipe_formula",message);
    private sealed class Parser(string text)
    {
        int index;
        void Space(){while(index<text.Length&&char.IsWhiteSpace(text[index]))index++;}
        public bool End{get{Space();return index==text.Length;}}
        public bool Peek(char token){Space();return index<text.Length&&text[index]==token;}
        public void Take(char token){if(!Peek(token))throw Invalid($"Expected '{token}' at character {index+1}.");index++;}
        public string Identifier()
        {
            Space();var start=index;
            while(index<text.Length&&(char.IsAsciiLetterOrDigit(text[index])||text[index]=='_'))index++;
            var value=text[start..index];if(!IdentifierValid(value))throw Invalid($"Expected an identifier at character {start+1}.");return value;
        }
        public ImmutableArray<RecipeContractWeight> Weights()=>Structured<ImmutableArray<RecipeContractWeight>>('[',']',"Contract weights");
        public ImmutableArray<RecipePanelWeight> PanelWeights()=>Structured<ImmutableArray<RecipePanelWeight>>('[',']',"Curve weights");
        public PublishedRecipeReference PublishedSource()=>Structured<PublishedRecipeReference>('{','}',"Published source");
        T Structured<T>(char opening,char closing,string label)
        {
            Space();var start=index;Take(opening);var depth=1;var quoted=false;var escaped=false;
            while(index<text.Length&&depth>0)
            {
                var token=text[index++];
                if(quoted){if(!escaped&&token=='"')quoted=false;escaped=!escaped&&token=='\\';continue;}
                if(token=='"')quoted=true;else if(token==opening)depth++;else if(token==closing)depth--;
            }
            if(depth!=0)throw Invalid(label+" is incomplete.");
            try{return JsonSerializer.Deserialize<T>(text[start..index],new JsonSerializerOptions(JsonSerializerDefaults.Web)) is {} value?value:throw Invalid(label+" requires an explicit typed value.");}
            catch(JsonException){throw Invalid(label+" requires the exact typed fields.");}
        }
        public string String()
        {
            Space();var start=index;Take('"');var escaped=false;
            while(index<text.Length)
            {
                var current=text[index++];
                if(!escaped&&current=='"')
                {
                    try{return JsonSerializer.Deserialize<string>(text[start..index])??throw Invalid("A parameter requires a string.");}
                    catch(JsonException){throw Invalid("Parameters must be valid JSON strings.");}
                }
                if(!escaped&&current=='\\')escaped=true;else escaped=false;
            }
            throw Invalid("A quoted parameter is incomplete.");
        }
    }
}
