import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', redirectTo: 'services', pathMatch: 'full' },
  { 
    path: 'services', 
    loadComponent: () => import('./pages/services/services').then(c => c.ServicesComponent) 
  },
  { 
    path: 'deploy', 
    loadComponent: () => import('./pages/deploy/deploy').then(c => c.DeployComponent) 
  },
  { 
    path: 'logs', 
    loadComponent: () => import('./pages/logs/logs').then(c => c.LogsComponent) 
  },
  {
    path: 'env-configs',
    loadComponent: () => import('./pages/env-configs/env-configs').then(c => c.EnvConfigsComponent)
  },
  { 
    path: 'settings', 
    loadComponent: () => import('./pages/settings/settings').then(c => c.SettingsComponent) 
  }
];
