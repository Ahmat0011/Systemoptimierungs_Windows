import re
import sys

def main():
    xaml_path = r"d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\MainWindow.xaml"
    
    with open(xaml_path, 'r', encoding='utf-8') as f:
        lines = f.readlines()
        
    print(f"Loaded {len(lines)} lines from {xaml_path}")
    
    # Find RecoveryDataGrid
    grid_line_idx = -1
    for idx, line in enumerate(lines):
        if 'x:Name="RecoveryDataGrid"' in line:
            grid_line_idx = idx
            break
            
    if grid_line_idx == -1:
        print("Error: Could not find RecoveryDataGrid in MainWindow.xaml")
        sys.exit(1)
        
    print(f"Found RecoveryDataGrid at line {grid_line_idx + 1}")
    
    # Find the first column within RecoveryDataGrid, which must be a DataGridTemplateColumn
    template_column_idx = -1
    for idx in range(grid_line_idx + 1, len(lines)):
        line = lines[idx]
        if '</DataGrid>' in line:
            break
        if '<DataGridTemplateColumn' in line:
            template_column_idx = idx
            break
            
    if template_column_idx == -1:
        print("Error: Could not find DataGridTemplateColumn inside RecoveryDataGrid")
        sys.exit(1)
        
    print(f"Found DataGridTemplateColumn at line {template_column_idx + 1}")
    
    # Verify indentation (starts at exactly 28 spaces)
    column_line = lines[template_column_idx]
    leading_spaces = len(column_line) - len(column_line.lstrip(' '))
    print(f"Indentation of DataGridTemplateColumn is {leading_spaces} spaces.")
    if leading_spaces != 28:
        print(f"FAIL: Indentation is {leading_spaces} instead of 28!")
        sys.exit(1)
    else:
        print("PASS: Indentation is exactly 28 spaces.")
        
    # Get all lines of the DataGridTemplateColumn block
    column_content_lines = []
    end_column_idx = -1
    for idx in range(template_column_idx, len(lines)):
        column_content_lines.append(lines[idx])
        if '</DataGridTemplateColumn>' in lines[idx]:
            end_column_idx = idx
            break
            
    if end_column_idx == -1:
        print("Error: Could not find </DataGridTemplateColumn>")
        sys.exit(1)
        
    column_content = "".join(column_content_lines)
    
    # Extract Header template and verify CheckBox
    header_match = re.search(r'<DataGridTemplateColumn\.Header>([\s\S]*?)</DataGridTemplateColumn\.Header>', column_content)
    if not header_match:
        print("Error: Could not find DataGridTemplateColumn.Header block")
        sys.exit(1)
        
    header_content = header_match.group(1)
    header_cb_match = re.search(r'<CheckBox\s+([^>]*?)/>', header_content)
    if not header_cb_match:
        print("Error: Could not find CheckBox in DataGridTemplateColumn.Header")
        sys.exit(1)
        
    header_cb_attrs = header_cb_match.group(1)
    print("Header CheckBox attributes found:", header_cb_attrs.strip())
    
    # Verify header binds to DataContext.IsAllFilesSelected
    if "DataContext.IsAllFilesSelected" not in header_cb_attrs:
        print("FAIL: Header CheckBox does not bind to DataContext.IsAllFilesSelected")
        sys.exit(1)
    else:
        print("PASS: Header CheckBox binds to DataContext.IsAllFilesSelected")
        
    # Verify header uses RelativeSource (AncestorType=DataGrid)
    rel_source_pattern = r'RelativeSource\s*=\s*\{\s*RelativeSource\s+AncestorType\s*=\s*(x:Type\s+)?DataGrid\s*\}'
    if not re.search(rel_source_pattern, header_cb_attrs):
        print("FAIL: RelativeSource AncestorType=DataGrid not found or incorrect in header CheckBox")
        sys.exit(1)
    else:
        print("PASS: Header CheckBox uses RelativeSource (AncestorType=DataGrid)")
        
    # Verify Path= is removed from the header CheckBox binding
    is_checked_binding_match = re.search(r'IsChecked\s*=\s*"\s*\{\s*Binding\s+([^}]+)\}', header_cb_attrs)
    if not is_checked_binding_match:
        print("FAIL: Could not find IsChecked binding in header CheckBox")
        sys.exit(1)
        
    binding_content = is_checked_binding_match.group(1)
    print("Binding content in header:", binding_content)
    if "Path=" in binding_content:
        print("FAIL: 'Path=' prefix is present in the header CheckBox binding")
        sys.exit(1)
    else:
        print("PASS: 'Path=' prefix is removed from the header CheckBox binding")
        
    # Extract CellTemplate and verify CheckBox
    cell_match = re.search(r'<DataGridTemplateColumn\.CellTemplate>([\s\S]*?)</DataGridTemplateColumn\.CellTemplate>', column_content)
    if not cell_match:
        print("Error: Could not find DataGridTemplateColumn.CellTemplate block")
        sys.exit(1)
        
    cell_content = cell_match.group(1)
    cell_cb_match = re.search(r'<CheckBox\s+([^>]*?)/>', cell_content)
    if not cell_cb_match:
        print("Error: Could not find CheckBox in DataGridTemplateColumn.CellTemplate")
        sys.exit(1)
        
    cell_cb_attrs = cell_cb_match.group(1)
    print("Cell CheckBox attributes found:", cell_cb_attrs.strip())
    
    # Verify cell CheckBox binds to IsSelected
    cell_binding_match = re.search(r'IsChecked\s*=\s*"\s*\{\s*Binding\s+([^}]+)\}', cell_cb_attrs)
    if not cell_binding_match:
        print("FAIL: Could not find IsChecked binding in cell CheckBox")
        sys.exit(1)
        
    cell_binding_content = cell_binding_match.group(1)
    print("Cell binding content:", cell_binding_content)
    
    if "IsSelected" not in cell_binding_content:
        print("FAIL: Cell CheckBox does not bind to IsSelected")
        sys.exit(1)
    else:
        print("PASS: Cell CheckBox binds to IsSelected")
        
    print("\nALL XAML STRUCTURE VERIFICATIONS PASSED SUCCESSFULLY!")

if __name__ == "__main__":
    main()
