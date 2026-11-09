using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AutoActivator.Config;
using ExcelDataReader;

namespace AutoActivator.Utils
{
    /// <summary>
    /// Utilitaire rapide et léger basé sur ExcelDataReader pour convertir les fichiers Excel (.xls / .xlsx) en CSV
    /// sans lancer de processus EXCEL.EXE (Interop) ni dépendre des pilotes OleDb ACE.
    /// </summary>
    public static class ExcelHelper
    {
        static ExcelHelper()
        {
            // Indispensable dans .NET Core / .NET 6 / .NET 8 pour lire les encodages Windows-1252 des fichiers .xls et .xlsx
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        /// <summary>
        /// Convertit le premier onglet d'un fichier Excel (.xls ou .xlsx) en fichier CSV séparé par des points-virgules.
        /// Utilise FileShare.ReadWrite pour pouvoir lire les fichiers réseau même s'ils sont ouverts par un autre utilisateur.
        /// </summary>
        /// <param name="excelFilePath">Chemin complet du fichier Excel source (.xls ou .xlsx).</param>
        /// <param name="outputDirectory">Dossier de destination du fichier CSV généré (par défaut Settings.OutputDir si vide).</param>
        /// <param name="filePrefix">Préfixe du fichier CSV généré.</param>
        /// <returns>Le chemin complet du fichier CSV généré.</returns>
        public static string ConvertExcelToCsv(
            string excelFilePath,
            string? outputDirectory = null,
            string filePrefix = "contrats_convertis")
        {
            if (string.IsNullOrWhiteSpace(excelFilePath))
                throw new ArgumentException("Le chemin du fichier Excel est obligatoire.", nameof(excelFilePath));

            if (!File.Exists(excelFilePath))
                throw new FileNotFoundException($"Fichier Excel introuvable : {excelFilePath}", excelFilePath);

            string targetDir = string.IsNullOrWhiteSpace(outputDirectory)
                ? Settings.OutputDir
                : outputDirectory;

            Directory.CreateDirectory(targetDir);

            string safePrefix = string.IsNullOrWhiteSpace(filePrefix) ? "contrats_convertis" : filePrefix.Trim();
            string baseName = Path.GetFileNameWithoutExtension(excelFilePath);
            string csvFilePath = Path.Combine(
                targetDir,
                $"{safePrefix}_{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 4)}.csv");

            using (var stream = File.Open(excelFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            using (var writer = new StreamWriter(csvFilePath, false, Encoding.UTF8))
            {
                var result = reader.AsDataSet();
                if (result.Tables.Count == 0)
                    throw new InvalidOperationException("Aucun onglet n'a été trouvé dans le fichier Excel.");

                DataTable table = result.Tables[0]; // Prend le premier onglet

                foreach (DataRow row in table.Rows)
                {
                    string[] fields = row.ItemArray
                        .Select(FormatExcelCellForCsv)
                        .ToArray();

                    // Ignore les lignes entièrement vides
                    if (fields.All(string.IsNullOrWhiteSpace))
                        continue;

                    writer.WriteLine(string.Join(";", fields));
                }
            }

            return csvFilePath;
        }

        /// <summary>
        /// Formate proprement la valeur d'une cellule Excel pour l'export CSV :
        /// - Évite la notation scientifique (ex: 1,23E+15) sur les longs Demand ID / numéros de contrat stockés en double
        /// - Remplace les points-virgules et retours à la ligne internes pour ne pas corrompre la structure CSV
        /// </summary>
        private static string FormatExcelCellForCsv(object? field)
        {
            if (field == null || field == DBNull.Value)
                return string.Empty;

            if (field is double d)
            {
                // Si c'est un entier (ex: Demand ID dans la colonne Value), on le formate sans décimales ni notation scientifique
                if (Math.Abs(d % 1) < double.Epsilon)
                    return d.ToString("0", CultureInfo.InvariantCulture);

                return d.ToString("0.################", CultureInfo.InvariantCulture);
            }

            if (field is float f)
            {
                if (Math.Abs(f % 1) < float.Epsilon)
                    return f.ToString("0", CultureInfo.InvariantCulture);

                return f.ToString("0.########", CultureInfo.InvariantCulture);
            }

            if (field is decimal dec)
            {
                return dec.ToString(CultureInfo.InvariantCulture);
            }

            if (field is DateTime dt)
            {
                return dt.TimeOfDay == TimeSpan.Zero
                    ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            return field.ToString()!
                .Replace(";", ",")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }
    }
}<UserControl x:Class="AutoActivator.Gui.Views.ActivationView"
              xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              Background="Transparent">

     <UserControl.Resources>
         <SolidColorBrush x:Key="TextPrimary" Color="#0F172A" />
         <SolidColorBrush x:Key="TextSecondary" Color="#64748B" />
         <SolidColorBrush x:Key="BorderDefault" Color="#E2E8F0" />
         <SolidColorBrush x:Key="SurfaceMuted" Color="#F8FAFC" />
         <SolidColorBrush x:Key="PrimaryBlue" Color="#2563EB" />
         <SolidColorBrush x:Key="SuccessGreen" Color="#10B981" />
         <SolidColorBrush x:Key="WarningAmber" Color="#F59E0B" />
         <SolidColorBrush x:Key="DangerRed" Color="#EF4444" />
         <SolidColorBrush x:Key="Branch23Purple" Color="#7C3AED" />

         <Style x:Key="CardStyle" TargetType="Border">
             <Setter Property="Background" Value="White" />
             <Setter Property="CornerRadius" Value="10" />
             <Setter Property="Padding" Value="22,18" />
             <Setter Property="BorderBrush" Value="{StaticResource BorderDefault}" />
             <Setter Property="BorderThickness" Value="1" />
         </Style>

         <Style x:Key="FieldLabelStyle" TargetType="TextBlock">
             <Setter Property="FontWeight" Value="SemiBold" />
             <Setter Property="FontSize" Value="12.5" />
             <Setter Property="Foreground" Value="#334155" />
             <Setter Property="Margin" Value="0,0,0,6" />
         </Style>

         <Style x:Key="InputComboBoxStyle" TargetType="ComboBox">
             <Setter Property="Padding" Value="8,7" />
             <Setter Property="FontSize" Value="13.5" />
             <Setter Property="BorderBrush" Value="#CBD5E1" />
             <Setter Property="Background" Value="White" />
             <Setter Property="MinHeight" Value="34" />
         </Style>

         <Style x:Key="InputTextBoxStyle" TargetType="TextBox">
             <Setter Property="Padding" Value="8,7" />
             <Setter Property="FontSize" Value="13.5" />
             <Setter Property="BorderBrush" Value="#CBD5E1" />
             <Setter Property="Background" Value="White" />
             <Setter Property="MinHeight" Value="34" />
             <Setter Property="VerticalContentAlignment" Value="Center" />
         </Style>

         <Style TargetType="TabItem">
             <Setter Property="Padding" Value="18,10" />
             <Setter Property="FontSize" Value="13.5" />
             <Setter Property="FontWeight" Value="Bold" />
             <Setter Property="Foreground" Value="#64748B" />
             <Setter Property="Background" Value="#E2E8F0" />
             <Setter Property="Margin" Value="0,0,6,0" />
             <Setter Property="Cursor" Value="Hand" />
             <Setter Property="Template">
                 <Setter.Value>
                     <ControlTemplate TargetType="TabItem">
                         <Border x:Name="TabBorder"
                                 Background="{TemplateBinding Background}"
                                 CornerRadius="8,8,0,0"
                                 Padding="{TemplateBinding Padding}"
                                 Margin="{TemplateBinding Margin}"
                                 BorderBrush="#CBD5E1"
                                 BorderThickness="1,1,1,0">
                             <ContentPresenter ContentSource="Header"
                                               HorizontalAlignment="Center"
                                               VerticalAlignment="Center" />
                         </Border>
                         <ControlTemplate.Triggers>
                             <Trigger Property="IsSelected" Value="True">
                                 <Setter TargetName="TabBorder" Property="Background" Value="White" />
                                 <Setter Property="Foreground" Value="#2563EB" />
                             </Trigger>
                             <Trigger Property="IsMouseOver" Value="True">
                                 <Setter TargetName="TabBorder" Property="Background" Value="#EFF6FF" />
                             </Trigger>
                         </ControlTemplate.Triggers>
                     </ControlTemplate>
                 </Setter.Value>
             </Setter>
         </Style>
     </UserControl.Resources>

     <ScrollViewer VerticalScrollBarVisibility="Auto"
                   HorizontalScrollBarVisibility="Disabled"
                   Padding="0,0,10,0">
         <Grid Margin="4">
             <Grid.RowDefinitions>
                 <RowDefinition Height="Auto" />
                 <RowDefinition Height="Auto" />
                 <RowDefinition Height="Auto" />
                 <RowDefinition Height="Auto" />
             </Grid.RowDefinitions>

             <Grid Grid.Row="0" Margin="0,0,0,18">
                 <StackPanel HorizontalAlignment="Left">
                     <TextBlock Text="Contract Activation Factory"
                                FontSize="24"
                                FontWeight="Bold"
                                Foreground="{StaticResource TextPrimary}" />
                     <TextBlock Text="Automatic ELIA routing, Mainframe JCL orchestration, Branch 23 activation and spool diagnostics."
                                FontSize="13"
                                Foreground="{StaticResource TextSecondary}"
                                Margin="0,3,0,0"
                                TextWrapping="Wrap" />
                 </StackPanel>

                 <Button Content="Help &amp; Docs"
                         Background="#EFF6FF"
                         Foreground="#2563EB"
                         BorderBrush="#BFDBFE"
                         BorderThickness="1"
                         Padding="14,7"
                         Cursor="Hand"
                         HorizontalAlignment="Right"
                         VerticalAlignment="Center"
                         FontWeight="Bold"
                         Click="BtnHelp_Click" />
             </Grid>

             <Border Grid.Row="1"
                     Style="{StaticResource CardStyle}"
                     Margin="0,0,0,16">
                 <StackPanel>
                     <TextBlock Text="1. Activation configuration"
                                FontWeight="Bold"
                                FontSize="15.5"
                                Foreground="{StaticResource TextPrimary}"
                                Margin="0,0,0,12" />

                     <Grid Margin="0,0,0,14">
                         <Grid.ColumnDefinitions>
                             <ColumnDefinition Width="*" />
                             <ColumnDefinition Width="24" />
                             <ColumnDefinition Width="*" />
                             <ColumnDefinition Width="24" />
                             <ColumnDefinition Width="*" />
                         </Grid.ColumnDefinitions>

                         <StackPanel Grid.Column="0">
                             <TextBlock Text="Input mode:" Style="{StaticResource FieldLabelStyle}" />
                             <ComboBox x:Name="CmbActivationInputMode"
                                       Style="{StaticResource InputComboBoxStyle}"
                                       SelectedIndex="0"
                                       SelectionChanged="ActivationInputMode_Changed">
                                 <ComboBoxItem Content="Autocomplete based on SQL info" Tag="SqlAutocomplete" />
                                 <ComboBoxItem Content="SQL autocomplete with manual overrides" Tag="SqlAutocompleteWithOverrides" />
                                 <ComboBoxItem Content="Manual variables" Tag="Manual" />
                             </ComboBox>
                             <TextBlock Text="SQL mode automatically resolves process, ChannelCode, Bank or Broker New Gen, BUCP, CMDPMT, premium and business date when Run is clicked."
                                        Margin="0,6,0,0"
                                        FontSize="11"
                                        Foreground="{StaticResource TextSecondary}"
                                        TextWrapping="Wrap" />
                         </StackPanel>

                         <StackPanel Grid.Column="2">
                             <TextBlock Text="Target Environment:" Style="{StaticResource FieldLabelStyle}" />
                             <ComboBox x:Name="CmbActEnv"
                                       Style="{StaticResource InputComboBoxStyle}"
                                       SelectedIndex="1"
                                       SelectionChanged="CmbActEnv_SelectionChanged">
                                 <ComboBoxItem Content="D000 - Development (IM7T)" Tag="D" />
                                 <ComboBoxItem Content="Q000 - Qualification (IM7C)" Tag="Q" />
                             </ComboBox>
                             <TextBlock Text="Q is selected by default. Use D only when its SQL and Mainframe configuration is validated."
                                        Margin="0,6,0,0"
                                        FontSize="11"
                                        Foreground="{StaticResource TextSecondary}"
                                        TextWrapping="Wrap" />
                         </StackPanel>

                         <StackPanel Grid.Column="4">
                             <TextBlock Text="CUS Code:" Style="{StaticResource FieldLabelStyle}" />
                             <TextBox x:Name="TxtActCus"
                                      Style="{StaticResource InputTextBoxStyle}"
                                      Margin="0,0,0,12"
                                      Text="MDEEM" />
                             <CheckBox x:Name="ChkActBranch23"
                                       Content="Force Branch 23 workflow"
                                       Margin="0,0,0,6"
                                       FontWeight="SemiBold"
                                       Foreground="#5B21B6"
                                       Cursor="Hand"
                                       ToolTip="Leave unchecked for automatic ELIA detection. Check only to force BR23." />
                             <TextBlock Text="Leave unchecked for automatic Branche 21/23 detection."
                                        Margin="22,0,0,0"
                                        FontSize="11"
                                        Foreground="{StaticResource TextSecondary}" />
                         </StackPanel>
                     </Grid>

                     <Border x:Name="AutomaticRoutingPanel"
                             Background="#EFF6FF"
                             BorderBrush="#BFDBFE"
                             BorderThickness="1"
                             CornerRadius="8"
                             Padding="14,12"
                             Margin="0,0,0,14">
                         <Grid>
                             <Grid.ColumnDefinitions>
                                 <ColumnDefinition Width="*" />
                                 <ColumnDefinition Width="Auto" />
                             </Grid.ColumnDefinitions>
                             <StackPanel Grid.Column="0">
                                 <TextBlock Text="Automatic payment routing"
                                            FontWeight="Bold"
                                            Foreground="#1E40AF" />
                                 <TextBlock Text="Bank ChannelCodes resolve to 00382/8. Broker New Gen ChannelCodes resolve to 80819/H. The resolution is performed automatically when activation starts."
                                            FontSize="11"
                                            Foreground="#1E40AF"
                                            Margin="0,3,12,0"
                                            TextWrapping="Wrap" />
                             </StackPanel>
                             <Button x:Name="BtnAnalyzeActivation"
                                     Grid.Column="1"
                                     Content="Analyze Contract"
                                     Padding="14,7"
                                     Background="#2563EB"
                                     Foreground="White"
                                     FontWeight="Bold"
                                     BorderThickness="0"
                                     Cursor="Hand"
                                     Click="BtnAnalyzeActivation_Click"
                                     ToolTip="Optional preflight. Run performs the same automatic resolution." />
                             <TextBox x:Name="TxtAutomaticRoutingPreview"
                                      Grid.ColumnSpan="2"
                                      Margin="0,52,0,0"
                                      MinHeight="74"
                                      MaxHeight="210"
                                      Padding="10"
                                      IsReadOnly="True"
                                      AcceptsReturn="True"
                                      TextWrapping="Wrap"
                                      VerticalScrollBarVisibility="Auto"
                                      Background="White"
                                      BorderBrush="#BFDBFE"
                                      Text="Optional preflight. You can run the activation directly." />
                         </Grid>
                     </Border>

                     <Border x:Name="ManualConfigurationPanel"
                             Background="{StaticResource SurfaceMuted}"
                             BorderBrush="{StaticResource BorderDefault}"
                             BorderThickness="1"
                             CornerRadius="8"
                             Padding="14,12"
                             Visibility="Collapsed">
                         <StackPanel>
                             <TextBlock Text="Manual variables and overrides"
                                        FontWeight="Bold"
                                        Foreground="#334155"
                                        Margin="0,0,0,10" />
                             <Grid>
                                 <Grid.ColumnDefinitions>
                                     <ColumnDefinition Width="*" />
                                     <ColumnDefinition Width="18" />
                                     <ColumnDefinition Width="*" />
                                     <ColumnDefinition Width="18" />
                                     <ColumnDefinition Width="*" />
                                 </Grid.ColumnDefinitions>

                                 <StackPanel Grid.Column="0">
                                     <TextBlock Text="Payment preset:" Style="{StaticResource FieldLabelStyle}" />
                                     <RadioButton x:Name="RbPresetBank"
                                                  Content="Bank (BUCP 00382, CMD 8)"
                                                  Margin="0,2,0,7"
                                                  FontWeight="SemiBold"
                                                  Cursor="Hand"
                                                  Checked="Preset_Checked" />
                                     <RadioButton x:Name="RbPresetBrokerNew"
                                                  Content="Broker New Gen (BUCP 80819, CMD H)"
                                                  Margin="0,2,0,7"
                                                  FontWeight="SemiBold"
                                                  Cursor="Hand"
                                                  Checked="Preset_Checked" />
                                     <RadioButton x:Name="RbPresetBrokerOld"
                                                  Content="Legacy Broker"
                                                  Visibility="Collapsed"
                                                  IsEnabled="False"
                                                  Checked="Preset_Checked" />
                                     <Button x:Name="BtnClearPreset"
                                             Content="Clear manual preset"
                                             Padding="10,5"
                                             HorizontalAlignment="Left"
                                             Click="BtnClearPreset_Click" />
                                 </StackPanel>

                                 <StackPanel Grid.Column="2">
                                     <TextBlock Text="Process:" Style="{StaticResource FieldLabelStyle}" />
                                     <ComboBox x:Name="CmbActChannel"
                                               Style="{StaticResource InputComboBoxStyle}"
                                               Margin="0,0,0,12"
                                               SelectedIndex="0"
                                               SelectionChanged="CmbActChannel_SelectionChanged">
                                         <ComboBoxItem Content="C01 - Standard Life / UL" Tag="C01" />
                                         <ComboBoxItem Content="C03 - Classic / Pension" Tag="C03" />
                                         <ComboBoxItem Content="C05 - Direct IMS / No Billing" Tag="C05" />
                                     </ComboBox>
                                     <TextBlock Text="Bureau Number (BUCP):" Style="{StaticResource FieldLabelStyle}" />
                                     <ComboBox x:Name="CmbActBucp"
                                               Style="{StaticResource InputComboBoxStyle}"
                                               IsEditable="True">
                                         <ComboBoxItem Content="00382" />
                                         <ComboBoxItem Content="80819" />
                                     </ComboBox>
                                 </StackPanel>

                                 <StackPanel Grid.Column="4">
                                     <TextBlock Text="Payment Command (CMDPMT):" Style="{StaticResource FieldLabelStyle}" />
                                     <ComboBox x:Name="CmbActCmdpmt"
                                               Style="{StaticResource InputComboBoxStyle}"
                                               Margin="0,0,0,12"
                                               IsEditable="True">
                                         <ComboBoxItem Content="8" />
                                         <ComboBoxItem Content="H" />
                                     </ComboBox>
                                     <TextBlock Text="BR23 fallback funds:" Style="{StaticResource FieldLabelStyle}" />
                                     <TextBox x:Name="TxtActBranch23Funds"
                                              Style="{StaticResource InputTextBoxStyle}"
                                              ToolTip="Optional comma-separated fallback fund codes." />
                                 </StackPanel>
                             </Grid>
                         </StackPanel>
                     </Border>
                 </StackPanel>
             </Border>

             <TabControl Grid.Row="2"
                         Background="Transparent"
                         BorderThickness="0"
                         Margin="0,0,0,16"
                         SelectedIndex="0">
                 <TabItem Header="Single Contract Activation">
                     <Border Style="{StaticResource CardStyle}" CornerRadius="0,10,10,10">
                         <StackPanel>
                             <TextBlock Text="Enter a Contract Number or ELIA Demand ID. Clicking Run automatically resolves the SQL plan and payment routing before any Mainframe job is submitted."
                                        Margin="0,0,0,14"
                                        Foreground="{StaticResource TextSecondary}"
                                        TextWrapping="Wrap" />
                             <StackPanel Margin="0,0,0,14" Orientation="Horizontal">
                                 <TextBlock Text="Search Mode:"
                                            FontWeight="Bold"
                                            Foreground="#334155"
                                            Margin="0,0,14,0"
                                            VerticalAlignment="Center" />
                                 <RadioButton x:Name="RbActSearchContract"
                                              Content="Contract Number"
                                              IsChecked="True"
                                              Margin="0,0,18,0"
                                              Cursor="Hand"
                                              FontWeight="SemiBold"
                                              Checked="ActInputType_Checked" />
                                 <RadioButton x:Name="RbActSearchDemand"
                                              Content="ELIA Demand ID"
                                              Cursor="Hand"
                                              FontWeight="SemiBold"
                                              Checked="ActInputType_Checked" />
                             </StackPanel>
                             <TextBlock Text="Contract Number / Demand ID:" Style="{StaticResource FieldLabelStyle}" />
                             <TextBox x:Name="TxtActContract"
                                      Style="{StaticResource InputTextBoxStyle}"
                                      Margin="0,0,0,18"
                                      FontSize="14"
                                      FontWeight="SemiBold" />
                             <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                                 <Button x:Name="BtnCancelSingleAct"
                                         Content="Cancel"
                                         Click="BtnCancelActivation_Click"
                                         Style="{DynamicResource CancelButtonStyle}"
                                         Width="125"
                                         Margin="0,0,10,0"
                                         IsEnabled="False" />
                                 <Button x:Name="BtnSkipPrimeSingleAct"
                                         Content="Skip Prime"
                                         Click="BtnSkipPrimeSingleAct_Click"
                                         Style="{DynamicResource ActionButtonStyle}"
                                         Background="#F59E0B"
                                         Width="170"
                                         Margin="0,0,10,0" />
                                 <Button x:Name="BtnRunSingleAct"
                                         Content="Run Single Activation"
                                         Click="BtnRunSingleActivation_Click"
                                         Style="{DynamicResource ActionButtonStyle}"
                                         Width="220" />
                             </StackPanel>
                         </StackPanel>
                     </Border>
                 </TabItem>

                 <TabItem Header="Batch Activation">
                     <Border Style="{StaticResource CardStyle}" CornerRadius="0,10,10,10">
                         <StackPanel>
                             <TextBlock Text="Select a CSV or Excel file. Every row is resolved independently, so Bank and Broker New Gen contracts can coexist in the same batch. Mainframe submissions remain sequential."
                                        Margin="0,0,0,14"
                                        Foreground="{StaticResource TextSecondary}"
                                        TextWrapping="Wrap" />
                             <StackPanel Margin="0,0,0,14" Orientation="Horizontal">
                                 <TextBlock Text="Input File Mode:"
                                            FontWeight="Bold"
                                            Foreground="#334155"
                                            Margin="0,0,14,0"
                                            VerticalAlignment="Center" />
                                 <RadioButton x:Name="RbBatchActSearchContract"
                                              Content="Contract Number List"
                                              Margin="0,0,18,0"
                                              Cursor="Hand"
                                              FontWeight="SemiBold"
                                              Checked="BatchActInputType_Checked" />
                                 <RadioButton x:Name="RbBatchActSearchDemand"
                                              Content="Demand ID List"
                                              IsChecked="True"
                                              Cursor="Hand"
                                              FontWeight="SemiBold"
                                              Checked="BatchActInputType_Checked" />
                             </StackPanel>
                             <TextBlock Text="Source Data File Path:" Style="{StaticResource FieldLabelStyle}" />
                             <Grid Margin="0,0,0,14">
                                 <Grid.ColumnDefinitions>
                                     <ColumnDefinition Width="*" />
                                     <ColumnDefinition Width="Auto" />
                                 </Grid.ColumnDefinitions>
                                 <TextBox x:Name="TxtBatchActCsv"
                                          Grid.Column="0"
                                          IsReadOnly="True"
                                          Style="{StaticResource InputTextBoxStyle}"
                                          Background="#F8FAFC"
                                          Margin="0,0,10,0" />
                                 <Button Grid.Column="1"
                                         Content="Browse..."
                                         Padding="16,8"
                                         Background="#334155"
                                         Foreground="White"
                                         FontWeight="Bold"
                                         BorderThickness="0"
                                         Cursor="Hand"
                                         Click="BtnBrowseActCsv_Click" />
                             </Grid>
                             <Border Background="#FFFBEB"
                                     BorderBrush="#FDE68A"
                                     BorderThickness="1"
                                     CornerRadius="6"
                                     Padding="10,8"
                                     Margin="0,0,0,12">
                                 <TextBlock FontSize="11.5" Foreground="#92400E" TextWrapping="Wrap">
                                     <Run FontWeight="Bold" Text="Optional override columns: " />
                                     <Run Text="BR23, AMOUNT, RUNDATE, BUCP, CMDPMT, PROCESS, CHANNEL and FUNDS. They are ignored in strict SQL mode." />
                                 </TextBlock>
                             </Border>
                             <Border Background="#EFF6FF"
                                     BorderBrush="#BFDBFE"
                                     BorderThickness="1"
                                     CornerRadius="8"
                                     Padding="14,12"
                                     Margin="0,0,0,18">
                                 <Grid>
                                     <Grid.RowDefinitions>
                                         <RowDefinition Height="Auto" />
                                         <RowDefinition Height="Auto" />
                                     </Grid.RowDefinitions>
                                     <Grid.ColumnDefinitions>
                                         <ColumnDefinition Width="*" />
                                         <ColumnDefinition Width="Auto" />
                                     </Grid.ColumnDefinitions>
                                     <WrapPanel Grid.Row="0" Grid.Column="0" VerticalAlignment="Center">
                                         <TextBlock Text="Batch progress:" FontWeight="Bold" Foreground="#1E3A8A" Margin="0,0,14,0" />
                                         <TextBlock Text="Completed " Foreground="#475569" />
                                         <TextBlock x:Name="TxtBatchProcessedCount" Text="0" FontWeight="Bold" Foreground="#047857" />
                                         <TextBlock Text=" / " Foreground="#64748B" />
                                         <TextBlock x:Name="TxtBatchPlannedCount" Text="0" FontWeight="Bold" Foreground="#0F172A" />
                                         <TextBlock Text="   Remaining " Margin="12,0,0,0" Foreground="#475569" />
                                         <TextBlock x:Name="TxtBatchRemainingCount" Text="0" FontWeight="Bold" Foreground="#D97706" />
                                         <TextBlock Text="   Current " Margin="12,0,0,0" Foreground="#475569" />
                                         <TextBlock x:Name="TxtBatchCurrentContract" Text="-" FontWeight="Bold" Foreground="#2563EB" />
                                     </WrapPanel>
                                     <StackPanel Grid.Row="0" Grid.Column="1" Orientation="Horizontal">
                                         <TextBlock x:Name="TxtBatchProgressStatus" Text="READY" FontWeight="Bold" Foreground="#64748B" Margin="0,0,12,0" />
                                         <TextBlock x:Name="TxtBatchProgressPercentage" Text="0%" FontWeight="Bold" FontSize="14" Foreground="#2563EB" />
                                     </StackPanel>
                                     <ProgressBar x:Name="ProgressBatchActivation"
                                                  Grid.Row="1"
                                                  Grid.ColumnSpan="2"
                                                  Height="8"
                                                  Margin="0,10,0,0"
                                                  Minimum="0"
                                                  Maximum="1"
                                                  Value="0"
                                                  Foreground="#2563EB"
                                                  Background="#DBEAFE" />
                                 </Grid>
                             </Border>
                             <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                                 <Button x:Name="BtnCancelBatchAct"
                                         Content="Cancel"
                                         Click="BtnCancelActivation_Click"
                                         Style="{DynamicResource CancelButtonStyle}"
                                         Width="125"
                                         Margin="0,0,10,0"
                                         IsEnabled="False" />
                                 <Button x:Name="BtnSkipPrimeBatchAct"
                                         Content="Skip Prime"
                                         Click="BtnSkipPrimeBatchAct_Click"
                                         Style="{DynamicResource ActionButtonStyle}"
                                         Background="#F59E0B"
                                         Width="170"
                                         Margin="0,0,10,0" />
                                 <Button x:Name="BtnRunBatchAct"
                                         Content="Run Batch Activation"
                                         Click="BtnRunBatchActivation_Click"
                                         Style="{DynamicResource ActionButtonStyle}"
                                         Width="220" />
                             </StackPanel>
                         </StackPanel>
                     </Border>
                 </TabItem>
             </TabControl>

             <Border Grid.Row="3" Style="{StaticResource CardStyle}" Padding="18,16">
                 <Grid>
                     <Grid.RowDefinitions>
                         <RowDefinition Height="Auto" />
                         <RowDefinition Height="Auto" />
                         <RowDefinition Height="Auto" />
                     </Grid.RowDefinitions>
                     <Grid Grid.Row="0" Margin="0,0,0,12">
                         <Grid.ColumnDefinitions>
                             <ColumnDefinition Width="*" />
                             <ColumnDefinition Width="Auto" />
                         </Grid.ColumnDefinitions>
                         <WrapPanel Grid.Column="0" Orientation="Horizontal" VerticalAlignment="Center">
                             <TextBlock Text="Live Activation and Diagnostic Monitor"
                                        FontSize="15.5"
                                        FontWeight="Bold"
                                        Foreground="{StaticResource TextPrimary}"
                                        Margin="0,0,16,0" />
                             <Border Background="#F1F5F9" BorderBrush="#CBD5E1" BorderThickness="1" CornerRadius="12" Padding="10,3" Margin="0,2,8,2">
                                 <StackPanel Orientation="Horizontal">
                                     <TextBlock Text="History: " FontSize="12" FontWeight="SemiBold" Foreground="#475569" />
                                     <TextBlock x:Name="TxtActTotalCount" Text="0" FontSize="12" FontWeight="Bold" />
                                 </StackPanel>
                             </Border>
                             <Border Background="#ECFDF5" BorderBrush="#A7F3D0" BorderThickness="1" CornerRadius="12" Padding="10,3" Margin="0,2,8,2">
                                 <StackPanel Orientation="Horizontal">
                                     <TextBlock Text="Activated: " FontSize="12" FontWeight="SemiBold" Foreground="#047857" />
                                     <TextBlock x:Name="TxtActSuccessCount" Text="0" FontSize="12" FontWeight="Bold" Foreground="#047857" />
                                 </StackPanel>
                             </Border>
                             <Border Background="#FFFBEB" BorderBrush="#FDE68A" BorderThickness="1" CornerRadius="12" Padding="10,3" Margin="0,2,8,2">
                                 <StackPanel Orientation="Horizontal">
                                     <TextBlock Text="Already / Pending: " FontSize="12" FontWeight="SemiBold" Foreground="#B45309" />
                                     <TextBlock x:Name="TxtActAlreadyCount" Text="0" FontSize="12" FontWeight="Bold" Foreground="#B45309" />
                                 </StackPanel>
                             </Border>
                             <Border Background="#FEF2F2" BorderBrush="#FECACA" BorderThickness="1" CornerRadius="12" Padding="10,3" Margin="0,2,8,2">
                                 <StackPanel Orientation="Horizontal">
                                     <TextBlock Text="Failed / Cancelled: " FontSize="12" FontWeight="SemiBold" Foreground="#B91C1C" />
                                     <TextBlock x:Name="TxtActFailCount" Text="0" FontSize="12" FontWeight="Bold" Foreground="#B91C1C" />
                                 </StackPanel>
                             </Border>
                         </WrapPanel>
                         <Button x:Name="BtnClearActHistory"
                                 Grid.Column="1"
                                 Content="Clear Log"
                                 Click="BtnClearActHistory_Click"
                                 Cursor="Hand"
                                 Padding="10,5"
                                 Background="White"
                                 Foreground="{StaticResource DangerRed}"
                                 BorderBrush="{StaticResource DangerRed}"
                                 BorderThickness="1" />
                     </Grid>
                     <TextBlock Grid.Row="1"
                                Text="Double-click a row to open SQL resolution, Bank/Broker routing, JCL timeline and Mainframe spool details."
                                FontSize="11"
                                Foreground="{StaticResource TextSecondary}"
                                Margin="0,0,0,8"
                                TextWrapping="Wrap" />
                     <ListView x:Name="ListActivationHistory"
                               Grid.Row="2"
                               Background="White"
                               BorderBrush="{StaticResource BorderDefault}"
                               BorderThickness="1"
                               AlternationCount="2"
                               MinHeight="190"
                               MaxHeight="440"
                               MouseDoubleClick="ListActivationHistory_MouseDoubleClick"
                               ScrollViewer.VerticalScrollBarVisibility="Auto"
                               ScrollViewer.HorizontalScrollBarVisibility="Auto"
                               ScrollViewer.CanContentScroll="True">
                         <ListView.ItemContainerStyle>
                             <Style TargetType="ListViewItem">
                                 <Setter Property="Padding" Value="4" />
                                 <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                                 <Style.Triggers>
                                     <Trigger Property="ItemsControl.AlternationIndex" Value="1">
                                         <Setter Property="Background" Value="#F8FAFC" />
                                     </Trigger>
                                     <DataTrigger Binding="{Binding Status}" Value="FAILED">
                                         <Setter Property="Background" Value="#FEF2F2" />
                                     </DataTrigger>
                                     <DataTrigger Binding="{Binding Status}" Value="CANCELLED">
                                         <Setter Property="Background" Value="#FFF7ED" />
                                     </DataTrigger>
                                     <DataTrigger Binding="{Binding Status}" Value="PENDING ASYNC LISA CREATION">
                                         <Setter Property="Background" Value="#FFFBEB" />
                                     </DataTrigger>
                                 </Style.Triggers>
                             </Style>
                         </ListView.ItemContainerStyle>
                         <ListView.View>
                             <GridView AllowsColumnReorder="True">
                                 <GridViewColumn Header="#" DisplayMemberBinding="{Binding RowNum}" Width="40" />
                                 <GridViewColumn Header="Time" DisplayMemberBinding="{Binding Time}" Width="65" />
                                 <GridViewColumn Header="Env" DisplayMemberBinding="{Binding Env}" Width="54" />
                                 <GridViewColumn Header="Input / Demand" DisplayMemberBinding="{Binding InputId}" Width="120" />
                                 <GridViewColumn Header="DB Contract" DisplayMemberBinding="{Binding ContractId}" Width="125" />
                                 <GridViewColumn Header="JCL ID" DisplayMemberBinding="{Binding JclContract}" Width="82" />
                                 <GridViewColumn Header="Product" DisplayMemberBinding="{Binding ProductCode}" Width="70" />
                                 <GridViewColumn Header="Process" DisplayMemberBinding="{Binding ProcessCode}" Width="62" />
                                 <GridViewColumn Header="Ch" DisplayMemberBinding="{Binding ChannelCode}" Width="42" />
                                 <GridViewColumn Header="Canal" Width="118">
                                     <GridViewColumn.CellTemplate>
                                         <DataTemplate>
                                             <TextBlock x:Name="BusinessChannelText"
                                                        Text="{Binding BusinessChannel}"
                                                        FontWeight="Bold"
                                                        Foreground="#64748B" />
                                             <DataTemplate.Triggers>
                                                 <DataTrigger Binding="{Binding BusinessChannel}" Value="BANK">
                                                     <Setter TargetName="BusinessChannelText" Property="Foreground" Value="#2563EB" />
                                                 </DataTrigger>
                                                 <DataTrigger Binding="{Binding BusinessChannel}" Value="BROKER_NEW_GEN">
                                                     <Setter TargetName="BusinessChannelText" Property="Foreground" Value="#7C3AED" />
                                                 </DataTrigger>
                                             </DataTemplate.Triggers>
                                         </DataTemplate>
                                     </GridViewColumn.CellTemplate>
                                 </GridViewColumn>
                                 <GridViewColumn Header="BUCP" DisplayMemberBinding="{Binding Bucp}" Width="66" />
                                 <GridViewColumn Header="CMD" DisplayMemberBinding="{Binding Cmdpmt}" Width="48" />
                                 <GridViewColumn Header="Run Date" DisplayMemberBinding="{Binding RunDate}" Width="82" />
                                 <GridViewColumn Header="BR23" Width="52">
                                     <GridViewColumn.CellTemplate>
                                         <DataTemplate>
                                             <TextBlock x:Name="Branch23Indicator" Text="NO" FontWeight="Bold" Foreground="#64748B" HorizontalAlignment="Center" />
                                             <DataTemplate.Triggers>
                                                 <DataTrigger Binding="{Binding IsBranch23}" Value="True">
                                                     <Setter TargetName="Branch23Indicator" Property="Text" Value="YES" />
                                                     <Setter TargetName="Branch23Indicator" Property="Foreground" Value="{StaticResource Branch23Purple}" />
                                                 </DataTrigger>
                                             </DataTemplate.Triggers>
                                         </DataTemplate>
                                     </GridViewColumn.CellTemplate>
                                 </GridViewColumn>
                                 <GridViewColumn Header="Premium" DisplayMemberBinding="{Binding Premium}" Width="84" />
                                 <GridViewColumn Header="Duration" DisplayMemberBinding="{Binding Duration}" Width="68" />
                                 <GridViewColumn Header="Status" Width="175">
                                     <GridViewColumn.CellTemplate>
                                         <DataTemplate>
                                             <TextBlock x:Name="ActivationRowStatus" Text="{Binding Status}" FontWeight="Bold" Foreground="#64748B" />
                                             <DataTemplate.Triggers>
                                                 <DataTrigger Binding="{Binding Status}" Value="SUCCESS">
                                                     <Setter TargetName="ActivationRowStatus" Property="Foreground" Value="#059669" />
                                                 </DataTrigger>
                                                 <DataTrigger Binding="{Binding Status}" Value="ALREADY ACTIVE">
                                                     <Setter TargetName="ActivationRowStatus" Property="Foreground" Value="#D97706" />
                                                 </DataTrigger>
                                                 <DataTrigger Binding="{Binding Status}" Value="PENDING ASYNC LISA CREATION">
                                                     <Setter TargetName="ActivationRowStatus" Property="Foreground" Value="#D97706" />
                                                 </DataTrigger>
                                                 <DataTrigger Binding="{Binding Status}" Value="FAILED">
                                                     <Setter TargetName="ActivationRowStatus" Property="Foreground" Value="#DC2626" />
                                                 </DataTrigger>
                                                 <DataTrigger Binding="{Binding Status}" Value="CANCELLED">
                                                     <Setter TargetName="ActivationRowStatus" Property="Foreground" Value="#EA580C" />
                                                 </DataTrigger>
                                             </DataTemplate.Triggers>
                                         </DataTemplate>
                                     </GridViewColumn.CellTemplate>
                                 </GridViewColumn>
                                 <GridViewColumn Header="Routing rule" DisplayMemberBinding="{Binding RoutingRule}" Width="245" />
                                 <GridViewColumn Header="Timeline and diagnostics" Width="360">
                                     <GridViewColumn.CellTemplate>
                                         <DataTemplate>
                                             <TextBlock Text="{Binding DiagnosticSummary}"
                                                        TextTrimming="CharacterEllipsis"
                                                        Foreground="#334155"
                                                        ToolTip="{Binding DiagnosticSummary}" />
                                         </DataTemplate>
                                     </GridViewColumn.CellTemplate>
                                 </GridViewColumn>
                             </GridView>
                         </ListView.View>
                     </ListView>
                 </Grid>
             </Border>
         </Grid>
     </ScrollViewer>
 </UserControl>
