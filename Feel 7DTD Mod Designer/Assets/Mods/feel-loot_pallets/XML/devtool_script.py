import os
import xml.etree.ElementTree as ET

def modify_xml_item_counts(xml_file_path, multiplier):
    # Check if the XML file exists
    if not os.path.exists(xml_file_path):
        print("The 'loot.xml' file does not exist in the 'Configs' directory. Please make sure you are running the script from the correct location and that the file structure is intact.")
        return

    tree = ET.parse(xml_file_path)
    root = tree.getroot()

    # Iterate over all items and blocks within lootcontainer tags
    for lootcontainer in root.iter('lootcontainer'):
        for item in lootcontainer.findall('item'):
            count = item.get('count')
            if count:
                # Handle ranges like "30,40" by applying the multiplier to each value
                if ',' in count:
                    min_count, max_count = map(int, count.split(','))
                    new_min_count = int(min_count * multiplier)
                    new_max_count = int(max_count * multiplier)
                    item.set('count', f"{new_min_count},{new_max_count}")
                else:
                    new_count = int(int(count) * multiplier)
                    item.set('count', str(new_count))

        for block in lootcontainer.findall('block'):
            count = block.get('count')
            if count:
                new_count = int(int(count) * multiplier)
                block.set('count', str(new_count))

    # Save the modified tree back to the same file or a new file
    tree.write(xml_file_path)  # Overwrites the existing file
    print(f"Item and block counts have been successfully adjusted by a factor of {multiplier} in '{xml_file_path}'.")

if __name__ == "__main__":
    xml_file_path = "./Config/loot.xml"  # Path relative to the script's location
    multiplier = float(input("Enter the multiplier to adjust item and block counts by (e.g., 1.2 to increase by 20%): "))
    modify_xml_item_counts(xml_file_path, multiplier)
