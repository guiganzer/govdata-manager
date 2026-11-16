using System.Diagnostics;
using System.Text.Json;
using GovData.Downloads.Core;
using GovData.Downloads.Desktop.Flow;

namespace GovData.Downloads.Desktop;

public sealed class GuidedPage : ContentPage
{
    readonly VerticalStackLayout body = new() { Spacing = 16 };
    readonly Label heading = new() { FontSize = 28, FontAttributes = FontAttributes.Bold };
    readonly Label steps = new() { FontSize = 15 };
    readonly Button back = Ui.Button("Voltar", "Back");
    readonly Button next = Ui.Button("Continuar", "Next");
    readonly Picker sources = new() { Title = "Escolha uma fonte", AutomationId = "SourcePicker" };
    readonly Entry destination = new() { AutomationId = "Destination", Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Dados públicos") };
    readonly Label status = new() { AutomationId = "RunStatus" };
    readonly ProgressBar progress = new() { ProgressColor = Color.FromArgb("164C80") };
    readonly ActivityIndicator spinner = new() { Color = Color.FromArgb("164C80") };
    readonly List<SourceProfile> profiles = [SourceProfile.Example];
    readonly string profilePath = Path.Combine(FileSystem.AppDataDirectory, "fontes-desktop.json");
    int stage; bool loaded; CancellationTokenSource? cancellation; FlowResult? result;
    public bool IsRunning { get; private set; }

    public GuidedPage()
    {
        Title = "Obter dados públicos"; BackgroundColor = Color.FromArgb("F3F6FA");
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level1);
        SemanticProperties.SetDescription(sources, "Fonte de dados públicos");
        SemanticProperties.SetDescription(destination, "Pasta de destino dos dados");
        var help = Ui.Button("Ajuda desta etapa", "Help");
        help.Clicked += async (_, _) => await DisplayAlertAsync("Como funciona", HelpText(), "Entendi");
        var top = new VerticalStackLayout { Spacing = 10, Children = { new Label { Text = "GOVDATA  /  ASSISTENTE", FontSize = 14, FontAttributes = FontAttributes.Bold }, heading, steps, help } };
        var footer = new Grid { ColumnDefinitions = [new(GridLength.Star),new(GridLength.Star)], ColumnSpacing = 16 };
        footer.Add(back,0); footer.Add(next,1);
        var layout = new Grid { Padding = 24, RowDefinitions = [new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)], RowSpacing = 20 };
        layout.Add(top,0,0); layout.Add(new ScrollView { Content = body },0,1); layout.Add(footer,0,2); Content = layout;
        back.Clicked += (_, _) => { if (!IsRunning && stage > 0) { stage--; Render(); } };
        next.Clicked += async (_, _) => await AdvanceAsync();
        Render();
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing(); if (loaded) return; loaded = true;
        try
        {
            if (File.Exists(profilePath))
            {
                var saved = JsonSerializer.Deserialize<SourceProfile[]>(await File.ReadAllTextAsync(profilePath), JsonStorage.Options) ?? [];
                foreach (var source in saved) { GuidedFlow.Validate(source); if (source.Id != SourceProfile.Example.Id) profiles.Add(source); }
            }
            RefreshSources();
        }
        catch (Exception ex) { RefreshSources(); await DisplayAlertAsync("Não foi possível ler os cadastros", ex.Message + "\nA fonte pronta continua disponível. O arquivo original não foi apagado.", "Entendi"); }
    }
    void RefreshSources(SourceProfile? selected = null)
    { sources.ItemsSource = null; sources.ItemsSource = profiles.ToArray(); sources.SelectedItem = selected ?? profiles[0]; }
    SourceProfile Selected => sources.SelectedItem as SourceProfile ?? throw new ArgumentException("Escolha uma fonte para continuar.");
    string HelpText() => stage switch {
        0 => "Fonte é o arquivo publicado por um órgão. Comece pelo IBGE: o cadastro e as regras de leitura já estão prontos. Para cadastrar outra fonte neste assistente, use um CSV com cabeçalho simples e endereço HTTPS direto. Fontes complexas continuam nas ferramentas avançadas.",
        1 => "Escolha onde guardar os originais e o resultado. O programa cria a subpasta GovData e mantém cada fonte separada. Nenhum banco de dados ou API da empresa é acessado. O motor mantém suas verificações de espaço livre; a organização usa uma estimativa antes de começar.",
        2 => "Revise o endereço antes de iniciar: ele será acessado para baixar o arquivo. Não é preciso editar JSON. A organização produz Parquet, formato tabular eficiente para reutilização. Use Voltar para corrigir qualquer escolha.",
        _ => "Aguarde o resultado. Interromper solicita cancelamento e preserva o que já foi confirmado; não significa que toda a organização esteja concluída. Use Tentar novamente para retomar o fluxo. A prévia mostra somente cinco linhas e até cinco colunas." };
    void Render()
    {
        body.Children.Clear(); steps.Text = $"Etapa {stage+1} de 4 • Fonte → Destino → Revisão → Resultado";
        back.IsEnabled = stage > 0 && !IsRunning;
        next.IsEnabled = !IsRunning;
        heading.Text = new[] { "Quais dados você quer obter?", "Onde deseja salvar?", "Confira antes de começar", "Acompanhe o resultado" }[stage];
        next.Text = stage switch { 2 => "Baixar e organizar", 3 => result is null ? "Tentar novamente" : "Obter outros dados", _ => "Continuar" };
        if (stage == 0)
        {
            body.Add(new Label { Text = "Escolha uma fonte cadastrada ou crie um cadastro guiado. O primeiro exemplo baixa apenas um arquivo pequeno do IBGE." });
            body.Add(Ui.LabelFor("Fonte cadastrada",sources)); body.Add(sources);
            var add = Ui.Button("Cadastrar uma fonte CSV", "RegisterSource");
            add.Clicked += async (_, _) => {
                var wizard = new SourceWizard(async source => {
                    var updated = profiles.Append(source).ToArray(); await GuidedFlow.SaveProfilesAsync(profilePath,updated);
                    profiles.Add(source); RefreshSources(source);
                });
                await Navigation.PushModalAsync(new NavigationPage(wizard));
            };
            body.Add(add); body.Add(new Label { Text = "A fonte pronta contém agregados municipais do Censo 2022. Sua versão e origem ficam registradas nos arquivos do resultado.", FontSize = 15 });
        }
        else if (stage == 1)
        {
            body.Add(Ui.LabelFor("Pasta de destino",destination)); body.Add(destination);
            var browse = Ui.Button("Escolher pasta…", "ChooseFolder");
            browse.Clicked += async (_, _) => {
                try {
                    var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
                    var native = (Microsoft.UI.Xaml.Window)Window.Handler!.PlatformView!;
                    WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(native));
                    var folder = await picker.PickSingleFolderAsync(); if (folder is not null) destination.Text = folder.Path;
                } catch (Exception ex) { await DisplayAlertAsync("Escolha a pasta", "Você também pode digitar o caminho completo.\n"+ex.Message,"Entendi"); }
            };
            body.Add(browse); body.Add(new Label { Text = "Os arquivos originais e os Parquets serão guardados dentro desta pasta. Escolha um local com permissão de gravação e espaço disponível." });
        }
        else if (stage == 2)
        {
            body.Add(new Label { Text = $"Fonte: {Selected.Name}\n\nÓrgão: {Selected.Agency}\n\nEndereço: {Selected.Url}\n\nDestino: {Path.Combine(destination.Text,"GovData",Selected.Id)}\n\nOperações: baixar → organizar em Parquet → conferir → mostrar prévia.", LineBreakMode = LineBreakMode.WordWrap });
            body.Add(new Label { Text = "Ao iniciar, você confirma o acesso ao endereço acima. A operação usa uma transferência por vez e não envia seus arquivos para esse endereço.", FontAttributes = FontAttributes.Bold });
        }
        else
        {
            body.Add(status); body.Add(spinner); body.Add(progress);
            if (result is not null)
            {
                body.Add(new Label { Text = $"Concluído: {result.Rows:N0} linhas • {result.ParquetFiles.Length} arquivo(s) Parquet", FontAttributes = FontAttributes.Bold });
                body.Add(new Label { Text = "Prévia (até 5 linhas e 5 colunas)", FontAttributes = FontAttributes.Bold });
                body.Add(new Label { Text = result.Preview, FontSize = 15, LineBreakMode = LineBreakMode.WordWrap });
                var open=Ui.Button("Abrir pasta dos resultados","OpenResults");
                open.Clicked += async (_,_) => { try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { result.Folder }, UseShellExecute=false }); } catch(Exception ex) { await DisplayAlertAsync("Pasta dos resultados",result.Folder+"\n"+ex.Message,"Entendi"); } };
                body.Add(open); body.Add(new Label { Text = result.Folder, FontSize = 14 });
            }
            else if (IsRunning) { var cancel=Ui.Button("Interromper com segurança","CancelRun"); cancel.Clicked += async (_,_)=>await RequestStopAsync();body.Add(cancel); }
        }
    }
    async Task AdvanceAsync()
    {
        try
        {
            if (stage==0) { GuidedFlow.Validate(Selected); stage++;Render(); }
            else if (stage==1) { if (string.IsNullOrWhiteSpace(destination.Text) || !Path.IsPathFullyQualified(destination.Text)) throw new ArgumentException("Escolha uma pasta ou informe seu caminho completo.");stage++;Render(); }
            else if (stage==2 || result is null) await RunAsync();
            else { stage=0;result=null;Render(); }
        }
        catch(Exception ex) { await DisplayAlertAsync("Confira as informações",ex.Message,"Corrigir"); }
    }
    public async Task RequestStopAsync()
    {
        if (IsRunning && await DisplayAlertAsync("Interromper?","Os arquivos já confirmados serão preservados. Aguarde a confirmação da interrupção antes de fechar.","Interromper","Continuar trabalhando")) cancellation?.Cancel();
    }
    async Task RunAsync()
    {
        if(IsRunning)return;
        var source=Selected;var folder=destination.Text;result=null;stage=3;IsRunning=true;
        cancellation=new CancellationTokenSource(TimeSpan.FromMinutes(10)); status.Text="Iniciando…";spinner.IsRunning=true;progress.Progress=0;Render();
        try {
            var updates=new Progress<FlowUpdate>(u=> { status.Text=$"{u.Stage}: {u.Message}"; if(u.Fraction is {} f)progress.Progress=Math.Clamp(f,0,1); });
            result=await Task.Run(()=>GuidedFlow.RunAsync(source,folder,updates,cancellation.Token));
            status.Text="Dados prontos para reutilização.";SemanticScreenReader.Default.Announce($"Concluído. {result.Rows} linhas organizadas.");
        }
        catch(OperationCanceledException) { status.Text="Operação interrompida ou tempo limite de dez minutos atingido. Você pode tentar novamente."; }
        catch(Exception ex) { status.Text="Não foi possível concluir. "+ex.Message; await DisplayAlertAsync("Vamos corrigir?",ex.Message+"\n\nConfira conexão, espaço livre e o cadastro. Os dados originais não foram apagados.","Entendi"); }
        finally { IsRunning=false;spinner.IsRunning=false;cancellation.Dispose();cancellation=null;Render(); }
    }
}

internal static class Ui
{
    public static Button Button(string text,string id)=>new(){Text=text,AutomationId=id};
    public static Label LabelFor(string text,VisualElement target)
    { SemanticProperties.SetDescription(target,text);return new Label{Text=text,FontAttributes=FontAttributes.Bold}; }
}
