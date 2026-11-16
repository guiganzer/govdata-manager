using GovData.Downloads.Desktop.Flow;

namespace GovData.Downloads.Desktop;

public sealed class SourceWizard : ContentPage
{
    readonly Entry name=new(){Placeholder="Ex.: Matrículas por município",AutomationId="SourceName"};
    readonly Entry agency=new(){Placeholder="Ex.: Ministério da Educação",AutomationId="AgencyName"};
    readonly Entry url=new(){Placeholder="https://…/arquivo.csv",Keyboard=Keyboard.Url,AutomationId="SourceUrl"};
    readonly Entry file=new(){Text="dados.csv",AutomationId="FileName"};
    readonly Entry columns=new(){Placeholder="codigo,nome,quantidade",AutomationId="Columns"};
    readonly Picker delimiter=new(){Title="Separador",ItemsSource=new[]{"Ponto e vírgula (;) ","Vírgula (,)","Tabulação"},SelectedIndex=0};
    readonly Picker encoding=new(){Title="Codificação",ItemsSource=new[]{"UTF-8","Windows-1252"},SelectedIndex=0};
    readonly Label title=new(){FontSize=26,FontAttributes=FontAttributes.Bold};
    readonly VerticalStackLayout body=new(){Spacing=12};
    readonly Button back=Ui.Button("Voltar","RegisterBack");
    readonly Button next=Ui.Button("Continuar","RegisterNext");
    readonly Func<SourceProfile,Task> save;
    readonly string id="csv-"+Guid.NewGuid().ToString("N");
    int step; bool saving;
    public SourceWizard(Func<SourceProfile,Task> save)
    {
        this.save=save;Title="Cadastrar fonte CSV";BackgroundColor=Color.FromArgb("F3F6FA");
        var help=Ui.Button("O que preencher nesta etapa?","RegisterHelp");
        help.Clicked+=async(_,_)=>await DisplayAlertAsync("Ajuda do cadastro",new[]{
            "Dê um nome que explique o conteúdo e identifique o órgão que o publica. Não precisa inventar códigos: o programa cria os identificadores.",
            "Copie o endereço direto de um CSV público. Uma página de busca ou um PDF não serve. Use HTTPS, sem senha ou parâmetros. O programa só fará o download depois da sua confirmação na tela principal.",
            "Abra o CSV em um editor e copie os nomes da primeira linha, separados por vírgulas neste campo. Neste cadastro simples, os nomes devem usar letras sem acento, números e sublinhado. O separador é o caractere que divide os valores no arquivo; UTF-8 é uma codificação comum. ZIP, Excel e layouts complexos exigem um cadastro avançado.",
            "Confira o resumo. Salvar cria o catálogo e as regras de leitura automaticamente; não inicia nenhum download. Depois escolha a pasta e confirme a execução."}[step],"Entendi");
        var cancel=Ui.Button("Cancelar cadastro","RegisterCancel");cancel.Clicked+=async(_,_)=>{if(!saving)await Navigation.PopModalAsync();};
        back.Clicked+=(_,_)=>{if(step>0&&!saving){step--;Render();}};
        next.Clicked+=async(_,_)=> {
            if(saving)return;
            try {
                if(step==0 && (string.IsNullOrWhiteSpace(name.Text)||string.IsNullOrWhiteSpace(agency.Text)))throw new ArgumentException("Preencha o nome da fonte e o órgão.");
                if(step==1 && (!Uri.TryCreate(url.Text?.Trim(),UriKind.Absolute,out var u)||u.Scheme!="https"))throw new ArgumentException("Informe um endereço HTTPS completo.");
                if(step>=2)GuidedFlow.Validate(Profile());
                if(step<3){step++;Render();}
                else{saving=true;next.IsEnabled=false;back.IsEnabled=false;cancel.IsEnabled=false;await save(Profile());await Navigation.PopModalAsync();}
            }catch(Exception ex){await DisplayAlertAsync("Revise o cadastro",ex.Message,"Corrigir");}
            finally {saving=false;next.IsEnabled=true;cancel.IsEnabled=true;back.IsEnabled=step>0;}
        };
        var buttons=new Grid{ColumnDefinitions=[new(GridLength.Star),new(GridLength.Star)],ColumnSpacing=12};buttons.Add(back,0);buttons.Add(next,1);
        Content=new ScrollView{Content=new VerticalStackLayout{Padding=28,Spacing=18,Children={title,help,body,buttons,cancel}}};Render();
    }
    SourceProfile Profile()=>new(id,name.Text?.Trim()??"",agency.Text?.Trim()??"",url.Text?.Trim()??"",file.Text?.Trim()??"",
        (columns.Text??"").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries),
        delimiter.SelectedIndex switch{1=>",",2=>"\t",_=>";"},encoding.SelectedIndex==1?"windows-1252":"utf-8");
    void Field(string label,View view){body.Add(Ui.LabelFor(label,view));body.Add(view);}
    void Render()
    {
        body.Children.Clear();title.Text=$"Cadastro • {step+1} de 4";back.IsEnabled=step>0;next.Text=step==3?"Salvar fonte":"Continuar";
        if(step==0){Field("Nome da fonte",name);Field("Órgão responsável",agency);}
        else if(step==1){Field("Endereço direto do CSV (HTTPS)",url);Field("Nome do arquivo no computador",file);}
        else if(step==2){Field("Colunas do cabeçalho, separadas por vírgula",columns);Field("Separador usado no arquivo",delimiter);Field("Codificação do texto",encoding);}
        else body.Add(new Label{Text=$"Fonte: {name.Text}\n\nÓrgão: {agency.Text}\n\nEndereço: {url.Text}\n\nArquivo: {file.Text}\n\nColunas: {columns.Text}\n\nSeparador: {delimiter.SelectedItem}\nCodificação: {encoding.SelectedItem}\n\nO cadastro é local. Nenhum download começa ao salvar."});
    }
}
