# Shortcut for Win

Clique com o botão do meio em qualquer ícone da barra de tarefas do Windows para abrir uma janelinha com o seu grupo de atalhos para aquele app.

*[Read in English](README.md)*

## Recursos

- **Botão do meio** em um ícone da barra abre a janelinha logo acima dele.
- Cada ícone da barra tem o seu **próprio grupo** de atalhos.
- Adicione programas **arrastando e soltando** arquivos, atalhos ou pastas, ou pelo botão **+** (lista de programas instalados com busca).
- **Arraste um atalho para fora** da janelinha, para a Área de Trabalho ou qualquer pasta, para movê-lo para lá (ele sai da janelinha).
- **Segue o tema do Windows**: modo claro/escuro, cor de destaque e transparência.
- Animações suaves: os itens sobem da barra e "vêm para frente" ao passar o mouse.
- O Explorador de Arquivos mantém o comportamento padrão do Windows (abre uma nova janela). Dá para excluir outros apps também.
- **Segue o idioma de exibição do Windows** (inglês, português, espanhol, francês e alemão; outros idiomas usam inglês).
- Fica na bandeja do sistema, com opção de iniciar com o Windows.

## Requisitos

- Windows 10 ou 11 (barra de tarefas na parte de baixo da tela)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (dispensável na versão self-contained)

## Como usar

1. Abra o `ShortcutForWin.exe`. Um ícone aparece na bandeja do sistema.
2. Clique com o botão do meio em um ícone da barra de tarefas.
3. Clique em **+** para escolher um programa, ou arraste algo para dentro da janelinha. Use o botão de pin para mantê-la aberta enquanto arrasta.
4. Passe o mouse sobre um atalho e clique no **×** para removê-lo, ou clique com o botão direito para abrir o menu de contexto do Windows (com a opção extra **Trocar…**).

### Excluir apps

Para manter o comportamento padrão do botão do meio em outros apps, crie `%AppData%\TaskbarFlyout\excluded.txt` com um nome por linha (sem diferenciar maiúsculas de minúsculas, comparado com o nome ou id do botão da barra):

```
# um nome por linha
Chrome
Spotify
```

### Log de diagnóstico

O log vem desligado. Para ligar, crie um arquivo vazio chamado `debug.flag` em `%AppData%\TaskbarFlyout\` e reinicie o programa. O log é gravado em `log.txt` na mesma pasta.

## Compilar o código

Requer o [.NET 8 SDK](https://dotnet.microsoft.com/download).

```
dotnet run
```

Versão final em um único arquivo:

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none
```

## Privacidade

O programa usa um hook global do mouse apenas para detectar cliques do botão do meio na barra de tarefas. Nada é gravado, armazenado ou enviado. Seus atalhos ficam salvos localmente em `%AppData%\TaskbarFlyout\groups.json`. O programa não faz conexões de rede.

## Limitações conhecidas

- Pensado para a barra de tarefas na parte de baixo da tela.
- Janelas abertas como administrador podem não receber o clique, a menos que este programa também rode como administrador.
- Como usa um hook global do mouse e não é assinado digitalmente, alguns antivírus ou o SmartScreen do Windows podem mostrar um aviso.

## Licença

[MIT](LICENSE)
