using System.Net;
using System.Text;
using System.Text.Json;
using GovData.Downloads.Desktop.Flow;
using GovData.Downloads.Server;

var checks=0;
void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
void Reject(SourceProfile p){try{GuidedFlow.Validate(p);}catch(ArgumentException){checks++;return;}catch(InvalidDataException){checks++;return;}throw new Exception("Cadastro inválido aceito.");}
var root=Path.GetFullPath(Path.Combine(".local","desktop-flow-tests",Guid.NewGuid().ToString("N")));
var source=new SourceProfile("csv-teste","Municípios de teste","Teste","https://example.org/municipios.csv","municipios.csv",["codigo","nome"]);
Reject(source with{Url="http://example.org/dados.csv"});
Reject(source with{Url="https://127.0.0.1/dados.csv"});
Reject(source with{FileName="../dados.csv"});
Reject(source with{Columns=["nome","NOME"]});
Reject(source with{Columns=["codigo);DROP TABLE x"]});
Reject(source with{Url="https://example.org/dados.csv?senha=123"});
Reject(SourceProfile.Example with{Url="https://example.org/substituido.zip"});
var updates=new Progress<FlowUpdate>(u=>Console.WriteLine($"{u.Stage}: {u.Message}"));
using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
var result=await GuidedFlow.RunCoreAsync(source,root,updates,timeout.Token,new HttpClient(new FixtureHandler()));
Check(result.Rows==2,"CSV deve conter duas linhas.");
Check(result.Preview.Contains("Sorriso"),"Prévia deve mostrar o arquivo realmente organizado.");
using(var doc=JsonDocument.Parse(File.ReadAllText(result.Manifest)))Check(doc.RootElement.GetProperty("status").GetString()=="READY","Manifesto deve ser READY.");
using(var db=DuckSql.Open(memoryMegabytes:512)){
 using var cmd=db.CreateCommand();cmd.CommandText=$"SELECT count(*) FROM read_parquet({DuckSql.Lit(GuidedFlow.DuckPath(result.ParquetFiles.Single()))})";
 Check(Convert.ToInt64(cmd.ExecuteScalar())==2,"Conteúdo Parquet diverge.");
}
if(args.Contains("--live-ibge")){
 var live=await GuidedFlow.RunAsync(SourceProfile.Example,root,updates,timeout.Token);
 Check(live.Rows==5570,"IBGE deve ter 5.570 municípios.");
 using var db=DuckSql.Open(memoryMegabytes:512);using var cmd=db.CreateCommand();
 cmd.CommandText=$"SELECT count(*) FROM read_parquet({DuckSql.Lit(GuidedFlow.DuckPath(live.ParquetFiles.Single()))})";
 Check(Convert.ToInt64(cmd.ExecuteScalar())==5570,"Contagem física IBGE diverge.");
 await File.WriteAllTextAsync(Path.Combine(root,"evidencia-ibge.json"),JsonSerializer.Serialize(live,new JsonSerializerOptions{WriteIndented=true}));
}
Console.WriteLine($"APROVADO: {checks} verificações. Evidências: {root}");
sealed class FixtureHandler:HttpMessageHandler
{
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Encoding.UTF8.GetBytes("codigo;nome\n5107925;Sorriso\n5103403;Cuiaba\n")),RequestMessage=request});
}
