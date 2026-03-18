import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TranslateModule } from '@ngx-translate/core';
import { EnvConfigsService } from '../../services/env-configs.service';
import { EnvConfigSet, EnvVariable } from '../../models/api-models';

@Component({
  selector: 'app-env-configs',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslateModule],
  templateUrl: './env-configs.html',
  styleUrl: './env-configs.less'
})
export class EnvConfigsComponent implements OnInit {
  private envConfigsSvc = inject(EnvConfigsService);

  configs = signal<EnvConfigSet[]>([]);
  loading = signal<boolean>(true);
  
  newConfig: EnvConfigSet = this.resetNewConfig();
  selectedConfig: EnvConfigSet | null = null;
  
  isModalOpen = false;
  isAddModalOpen = false;

  ngOnInit() {
    this.loadData();
  }

  loadData() {
    this.loading.set(true);
    this.envConfigsSvc.getAll().then(data => {
      this.configs.set(data);
      this.loading.set(false);
    }).catch(err => {
      console.error('Error fetching env configs:', err);
      this.loading.set(false);
    });
  }

  openAddModal() {
    this.newConfig = this.resetNewConfig();
    this.isAddModalOpen = true;
  }

  closeAddModal() {
    this.isAddModalOpen = false;
  }

  addVariable(config: EnvConfigSet) {
    config.variables.push({ key: '', value: '' });
  }

  removeVariable(config: EnvConfigSet, index: number) {
    config.variables.splice(index, 1);
  }

  saveNewConfig() {
    if (!this.newConfig.name || !this.newConfig.environmentId) return;

    this.envConfigsSvc.create(this.newConfig).then(() => {
      this.closeAddModal();
      this.loadData();
    });
  }

  openEditModal(config: EnvConfigSet) {
    this.selectedConfig = JSON.parse(JSON.stringify(config));
    if (!this.selectedConfig?.variables) this.selectedConfig!.variables = [];
    this.isModalOpen = true;
  }

  closeModal() {
    this.isModalOpen = false;
    this.selectedConfig = null;
  }

  updateConfig() {
    if (!this.selectedConfig || !this.selectedConfig.id) return;
    this.envConfigsSvc.update(this.selectedConfig.id, this.selectedConfig).then(() => {
      this.closeModal();
      this.loadData();
    });
  }

  deleteConfig(id: string) {
    if (!confirm('Are you sure you want to delete this configuration set?')) return;
    this.envConfigsSvc.delete(id).then(() => {
      this.closeModal();
      this.loadData();
    });
  }

  addFileRename(config: EnvConfigSet) {
    if (!config.fileRenames) config.fileRenames = [];
    config.fileRenames.push({ sourceFileName: '', targetFileName: '' });
  }

  removeFileRename(config: EnvConfigSet, index: number) {
    config.fileRenames.splice(index, 1);
  }

  private resetNewConfig(): EnvConfigSet {
    return { name: '', environmentId: '', targetFileName: 'appsettings.json', variables: [], fileRenames: [] };
  }
}
