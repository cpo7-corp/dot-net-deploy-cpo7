import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TranslateModule } from '@ngx-translate/core';
import { SettingsService } from '../../services/settings.service';
import { AppSettings, VpsSettings } from '../../models/api-models';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslateModule],
  templateUrl: './settings.html',
  styleUrl: './settings.less'
})
export class SettingsComponent implements OnInit {
  private settingsSvc = inject(SettingsService);

  activeTab = signal<'git' | 'vps'>('git');
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

  setTab(tab: 'git' | 'vps') {
    this.activeTab.set(tab);
  }

  addVpsEnvironment() {
    const newVps: VpsSettings = {
      name: 'New Environment',
      host: '',
      username: '',
      password: '',
      port: 22,
      isLocal: false
    };
    this.settings.update(s => ({
      ...s,
      vpsEnvironments: [...(s.vpsEnvironments || []), newVps]
    }));
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
}
