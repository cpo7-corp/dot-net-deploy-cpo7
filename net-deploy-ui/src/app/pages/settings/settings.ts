import { ChangeDetectorRef, Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TranslateModule } from '@ngx-translate/core';
import { SettingsService } from '../../services/settings.service';
import { AppSettings, VpsSettings } from '../../models/api-models';
import { MaintenanceService } from '../../services/maintenance.service';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslateModule],
  templateUrl: './settings.html',
  styleUrl: './settings.less'
})
export class SettingsComponent implements OnInit {
  private settingsSvc = inject(SettingsService);
  private maintenanceSvc = inject(MaintenanceService);
  private cdr = inject(ChangeDetectorRef);

  activeTab = signal<'git' | 'vps' | 'database'>('git');
  settings = signal<AppSettings>({
    git: { token: '', localBaseDir: 'C:\\deploy-temp' },
    vpsEnvironments: []
  });

  showLoading = signal<boolean>(true);
  saving = signal<boolean>(false);
  successMsg = signal<string>('');

  // Import state
  selectedFile = signal<{ name: string; size: number; data: any } | null>(null);
  importStatus = signal<string>('');
  pasteJsonText = '';

  ngOnInit() {
    this.settingsSvc.getSettings().subscribe({
      next: (data) => {
        if (data.id) {
          if (!data.vpsEnvironments) data.vpsEnvironments = [];
          this.settings.set(data);
        }
        this.showLoading.set(false);
      }
    });
  }

  setTab(tab: 'git' | 'vps' | 'database') {
    this.activeTab.set(tab);
  }

  addVpsEnvironment() {
    const newVps: VpsSettings = {
      name: 'New Environment',
      host: '',
      username: '',
      password: '',
      port: 22,
      isLocal: false,
      environmentTag: '',
      defaultDeployBasePath: '',
      serverType: 'Windows',
      defaultDockerBasePath: '/opt/net-deploy',
      useSudoDocker: false,
      dockerRegistryUrl: '',
      dockerRegistryUsername: '',
      dockerRegistryPassword: '',
      sharedVariables: [],
      sharedFileRenames: []
    };
    this.settings.update(s => ({
      ...s,
      vpsEnvironments: [...(s.vpsEnvironments || []), newVps]
    }));
  }

  addSharedVariable(vps: VpsSettings) {
    if (!vps.sharedVariables) vps.sharedVariables = [];
    vps.sharedVariables.push({ key: '', value: '' });
  }

  removeSharedVariable(vps: VpsSettings, index: number) {
    vps.sharedVariables?.splice(index, 1);
  }

  addSharedFileRename(vps: VpsSettings) {
    if (!vps.sharedFileRenames) vps.sharedFileRenames = [];
    vps.sharedFileRenames.push({ sourceFileName: '', targetFileName: '' });
  }

  removeSharedFileRename(vps: VpsSettings, index: number) {
    vps.sharedFileRenames?.splice(index, 1);
  }

  removeVpsEnvironment(index: number) {
    this.settings.update(s => ({
      ...s,
      vpsEnvironments: s.vpsEnvironments.filter((_, i) => i !== index)
    }));
  }

  saveGlobalSettings() {
    this.saving.set(true);
    this.successMsg.set('');
    this.settingsSvc.saveSettings(this.settings()).subscribe({
      next: (res) => {
        this.settings.set(res);
        this.saving.set(false);
        this.successMsg.set('Settings saved successfully.');
        setTimeout(() => this.successMsg.set(''), 3000);
      },
      error: () => this.saving.set(false)
    });
  }

  exportDb() {
    this.maintenanceSvc.exportDatabase().subscribe(data => {
      const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
      const url = window.URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `net-deploy-settings-${new Date().toISOString().split('T')[0]}.json`;
      a.click();
      window.URL.revokeObjectURL(url);
    });
  }

  async onImportDb(event: any) {
    const input = event.target as HTMLInputElement;
    const file = input?.files?.[0];
    if (!file) return;

    this.importStatus.set('Reading file: ' + file.name + ' (' + file.size + ' bytes)...');
    this.cdr.detectChanges();

    try {
      const text = await file.text();
      const cleanText = text.replace(/^\uFEFF/, '').trim();
      const data = JSON.parse(cleanText);

      this.selectedFile.set({
        name: file.name,
        size: file.size,
        data: data
      });
      this.importStatus.set('');
      this.cdr.detectChanges();
    } catch (err: any) {
      this.importStatus.set('Error reading file: ' + (err?.message || 'Invalid JSON'));
      this.selectedFile.set(null);
      this.cdr.detectChanges();
    } finally {
      if (input) input.value = '';
    }
  }

  doImport() {
    const item = this.selectedFile();
    if (!item?.data) return;

    this.importStatus.set('Importing into database...');
    this.cdr.detectChanges();

    this.maintenanceSvc.importDatabase(item.data).subscribe({
      next: () => {
        this.importStatus.set('Imported successfully! Reloading...');
        this.selectedFile.set(null);
        this.cdr.detectChanges();
        setTimeout(() => window.location.reload(), 1200);
      },
      error: (err) => {
        this.importStatus.set('Import failed: ' + (err.error?.message || err.message));
        this.cdr.detectChanges();
      }
    });
  }

  importFromPaste() {
    if (!this.pasteJsonText.trim()) return;
    try {
      const cleanText = this.pasteJsonText.replace(/^\uFEFF/, '').trim();
      const data = JSON.parse(cleanText);
      this.selectedFile.set({
        name: 'Pasted JSON Data',
        size: cleanText.length,
        data: data
      });
      this.importStatus.set('');
      this.cdr.detectChanges();
    } catch (err: any) {
      this.importStatus.set('Error parsing pasted JSON: ' + (err?.message || 'Invalid JSON format'));
      this.selectedFile.set(null);
      this.cdr.detectChanges();
    }
  }

  cancelImport() {
    this.selectedFile.set(null);
    this.importStatus.set('');
    this.cdr.detectChanges();
  }
}
