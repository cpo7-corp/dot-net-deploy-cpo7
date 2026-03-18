import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TranslateModule } from '@ngx-translate/core';
import { EnvConfigsService } from '../../services/env-configs.service';
import { EnvConfigSet } from '../../models/api-models';

@Component({
  selector: 'app-env-configs',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslateModule],
  templateUrl: './env-configs.html',
  styleUrl: './env-configs.less'
})
export class EnvConfigsComponent implements OnInit {
  private configSvc = inject(EnvConfigsService);

  configs = signal<EnvConfigSet[]>([]);
  loading = signal<boolean>(true);

  isModalOpen = false;
  selectedConfig: EnvConfigSet | null = null;
  newConfig: Partial<EnvConfigSet> = this.resetNewConfig();

  ngOnInit() {
    this.loadConfigs();
  }

  loadConfigs() {
    this.loading.set(true);
    this.configSvc.getAll().subscribe({
      next: (data) => {
        this.configs.set(data);
        this.loading.set(false);
      },
      error: () => this.loading.set(false)
    });
  }

  openAddModal() {
    this.selectedConfig = null;
    this.newConfig = this.resetNewConfig();
    this.isModalOpen = true;
  }

  openEditModal(config: EnvConfigSet) {
    this.selectedConfig = JSON.parse(JSON.stringify(config));
    this.isModalOpen = true;
  }

  closeModal() {
    this.isModalOpen = false;
    this.selectedConfig = null;
  }

  saveConfig() {
    const config = this.selectedConfig || (this.newConfig as EnvConfigSet);
    
    // Trim descriptive fields with null-safety
    config.name = config.name?.trim() ?? '';
    config.sourceFileName = config.sourceFileName?.trim() ?? '';
    config.targetFileName = config.targetFileName?.trim() ?? '';

    // Trim variables with null-safety
    if (config.variables) {
      config.variables = config.variables.map(v => ({
        key: v.key?.trim() ?? '',
        value: v.value?.trim() ?? ''
      })).filter(v => !!v.key); // Only filter if key is not empty
    }

    if (this.selectedConfig) {
      this.configSvc.update(this.selectedConfig.id!, config).subscribe(() => {
        this.loadConfigs();
        this.closeModal();
      });
    } else {
      this.configSvc.create(config).subscribe(() => {
        this.loadConfigs();
        this.closeModal();
      });
    }
  }

  deleteConfig(id: string) {
    if (!confirm('Are you sure?')) return;
    this.configSvc.delete(id).subscribe(() => this.loadConfigs());
  }

  addVariable() {
    const config = this.selectedConfig || this.newConfig;
    if (!config.variables) config.variables = [];
    config.variables.push({ key: '', value: '' });
  }

  removeVariable(index: number) {
    const config = this.selectedConfig || this.newConfig;
    config.variables?.splice(index, 1);
  }

  private resetNewConfig(): Partial<EnvConfigSet> {
    return { name: '', sourceFileName: '', targetFileName: '', variables: [] };
  }
}
