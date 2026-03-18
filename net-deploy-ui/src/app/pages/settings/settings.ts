import { Component, inject, OnInit, signal } from '@angular/core';
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

  activeTab = signal<'git' | 'vps' | 'database'>('git');
  settings = signal<AppSettings>({
    git: { token: '', localBaseDir: 'C:\\deploy-temp' },
    vpsEnvironments: []
  });

  showLoading = signal<boolean>(true);
  saving = signal<boolean>(false);
  successMsg = signal<string>('');

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
      id: '',
      name: 'New Environment',
      host: '',
      username: '',
      password: '',
      port: 22,
      isLocal: false,
      environmentTag: '',
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

  onImportDb(event: any) {
    const file = event.target.files[0];
    if (!file) return;

    const reader = new FileReader();
    reader.onload = (e: any) => {
      try {
        const data = JSON.parse(e.target.result);
        if (confirm('Are you sure? This will replace ALL existing settings, services and environment configurations!')) {
          this.maintenanceSvc.importDatabase(data).subscribe({
            next: () => {
              alert('Imported successfully! The page will now reload.');
              window.location.reload();
            },
            error: (err) => {
              alert('Import failed: ' + (err.error?.message || err.message));
            }
          });
        }
      } catch (err) {
        alert('Invalid JSON file.');
      }
    };
    reader.readAsText(file);
    event.target.value = ''; // Reset input
  }
}
